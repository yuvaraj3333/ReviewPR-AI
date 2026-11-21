import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { TextDecoder } from 'node:util';
import { safeRelativePath } from './diffValidation';

const MAX_UNTRACKED_FILE_BYTES = 32 * 1024;

const EXCLUDED_DIRECTORIES = new Set([
    '.git', '.vscode-test', 'node_modules', 'bin', 'obj',
    'dist', 'out', 'coverage', 'publish'
]);

const EXCLUDED_FILE_NAMES = new Set([
    '.npmrc', '.pypirc', '.netrc', 'nuget.config',
    'id_rsa', 'id_ed25519'
]);

const SECRET_FILE_PATTERN =
    /^(?:\.env(?:\..*)?|secrets?(?:\..*)?|credentials?(?:\..*)?)$/i;

const PRIVATE_KEY_PATTERN =
    /\.(?:pem|key|p12|pfx|p7b|p7c|jks|keystore)$/i;

export interface UntrackedDiffResult {
    diff: string;
    skipped: number;
}

export type GitRunner = (
    args: string[],
    cwd: string
) => Promise<string>;

function isExcludedPath(relativePath: string): boolean {
    const parts = relativePath.split('/');
    const fileName = parts[parts.length - 1];

    return parts.some(part =>
        EXCLUDED_DIRECTORIES.has(part.toLowerCase()) ||
        /^backup-/i.test(part)
    ) ||
        EXCLUDED_FILE_NAMES.has(fileName.toLowerCase()) ||
        SECRET_FILE_PATTERN.test(fileName) ||
        PRIVATE_KEY_PATTERN.test(fileName);
}

async function resolveSafeRegularFile(
    root: string,
    relativePath: string
): Promise<string | undefined> {
    const absoluteRoot = await fs.realpath(root);
    let current = absoluteRoot;

    const parts = relativePath.split('/');

    for (let i = 0; i < parts.length; i++) {
        current = path.join(current, parts[i]);

        const metadata = await fs.lstat(current);

        if (metadata.isSymbolicLink()) {
            return undefined;
        }

        if (i < parts.length - 1 && !metadata.isDirectory()) {
            return undefined;
        }

        if (i === parts.length - 1 && !metadata.isFile()) {
            return undefined;
        }
    }

    const relative = path.relative(absoluteRoot, current);

    if (
        !relative ||
        relative === '..' ||
        relative.startsWith(`..${path.sep}`) ||
        path.isAbsolute(relative)
    ) {
        return undefined;
    }

    return current;
}

export async function buildUntrackedDiff(
    root: string,
    runGit: GitRunner,
    remainingCharacters: number
): Promise<UntrackedDiffResult> {
    const result: string[] = [];
    let skipped = 0;
    let totalCharacters = 0;

    if (remainingCharacters <= 0) {
        return { diff: '', skipped: 0 };
    }

    const listing = await runGit(
        [
            '-c',
            'core.quotepath=false',
            'ls-files',
            '--others',
            '--exclude-standard',
            '-z'
        ],
        root
    );

    const paths = listing
        .split('\0')
        .filter(Boolean)
        .sort((a, b) => a.localeCompare(b));

    const decoder = new TextDecoder('utf-8', { fatal: true });

    for (const relativePath of paths) {
        const safePath = safeRelativePath(relativePath);

        if (!safePath || isExcludedPath(safePath)) {
            skipped++;
            continue;
        }

        try {
            const absolutePath = await resolveSafeRegularFile(
                root,
                safePath
            );

            if (!absolutePath) {
                skipped++;
                continue;
            }

            const metadata = await fs.stat(absolutePath);

            if (
                metadata.size === 0 ||
                metadata.size > MAX_UNTRACKED_FILE_BYTES
            ) {
                skipped++;
                continue;
            }

            const handle = await fs.open(
                absolutePath,
                'r'
            );

            let bytes: Buffer;

            try {
                const currentMetadata = await handle.stat();

                if (
                    !currentMetadata.isFile() ||
                    currentMetadata.size === 0 ||
                    currentMetadata.size > MAX_UNTRACKED_FILE_BYTES
                ) {
                    skipped++;
                    continue;
                }

                bytes = await handle.readFile();
            } finally {
                await handle.close();
            }

            if (bytes.length === 0 || bytes.length > MAX_UNTRACKED_FILE_BYTES ||
                bytes.includes(0)) {
                skipped++;
                continue;
            }

            let content: string;

            try {
                content = decoder.decode(bytes);
            } catch {
                skipped++;
                continue;
            }

            const normalized = content
                .replace(/\r\n/g, '\n')
                .replace(/\r/g, '\n');

            const hasFinalNewline = normalized.endsWith('\n');
            const lines = normalized.split('\n');

            if (hasFinalNewline) {
                lines.pop();
            }

            if (lines.length === 0) {
                skipped++;
                continue;
            }

            const section = [
                `diff --git a/${safePath} b/${safePath}`,
                'new file mode 100644',
                '--- /dev/null',
                `+++ b/${safePath}`,
                `@@ -0,0 +1,${lines.length} @@`,
                ...lines.map(line => `+${line}`),
                ...(hasFinalNewline
                    ? []
                    : ['\\ No newline at end of file']),
                ''
            ].join('\n');

            if (totalCharacters + section.length > remainingCharacters) {
                skipped++;
                continue;
            }

            result.push(section);
            totalCharacters += section.length;
        } catch {
            skipped++;
        }
    }

    return {
        diff: result.join(''),
        skipped
    };
}
