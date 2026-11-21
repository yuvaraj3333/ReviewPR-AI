import * as assert from 'assert';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import {
    buildUntrackedDiff,
    GitRunner
} from '../untrackedDiff';
import { addedLinesByFile } from '../diffValidation';

async function createTestRepository(
    files: Record<string, string | Buffer>
) {
    const root = await fs.mkdtemp(
        path.join(os.tmpdir(), 'reviewpr-untracked-')
    );

    for (const [relativePath, content] of Object.entries(files)) {
        const absolutePath = path.join(
            root,
            ...relativePath.split('/')
        );

        await fs.mkdir(path.dirname(absolutePath), {
            recursive: true
        });

        await fs.writeFile(absolutePath, content);
    }

    const paths = Object.keys(files);

    const runGit: GitRunner = async (args, cwd) => {
        assert.strictEqual(cwd, root);
        assert.ok(args.includes('ls-files'));
        assert.ok(args.includes('--others'));
        assert.ok(args.includes('--exclude-standard'));
        assert.ok(args.includes('-z'));

        return paths.length ? `${paths.join('\0')}\0` : '';
    };

    return {
        root,
        runGit,
        cleanup: () => fs.rm(root, {
            recursive: true,
            force: true
        })
    };
}

suite('ReviewPR AI - Untracked Diff Collection', () => {
    test('includes an untracked text file as a valid unified diff', async () => {
        const repo = await createTestRepository({
            'src/new.ts': 'const value = 10;\nconsole.log(value);\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.strictEqual(result.skipped, 0);
            assert.ok(result.diff.includes('+++ b/src/new.ts'));
            assert.ok(result.diff.includes('+const value = 10;'));

            const lines = addedLinesByFile(result.diff).get('src/new.ts');

            assert.ok(lines?.has(1));
            assert.ok(lines?.has(2));
        } finally {
            await repo.cleanup();
        }
    });

    test('includes multiple untracked files', async () => {
        const repo = await createTestRepository({
            'src/first.ts': 'const first = 1;\n',
            'src/second.ts': 'const second = 2;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.ok(result.diff.includes('+++ b/src/first.ts'));
            assert.ok(result.diff.includes('+++ b/src/second.ts'));
            assert.strictEqual(result.skipped, 0);
        } finally {
            await repo.cleanup();
        }
    });

    test('excludes secrets, credentials and backup directories', async () => {
        const repo = await createTestRepository({
            '.env': 'API_KEY=secret\n',
            'secrets.json': '{"key":"secret"}\n',
            'credentials.json': '{"password":"secret"}\n',
            'src/.env.local': 'TOKEN=secret\n',
            'backup-old/source.ts': 'const old = true;\n',
            'src/valid.ts': 'const valid = true;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.ok(result.diff.includes('+++ b/src/valid.ts'));
            assert.ok(!result.diff.includes('.env'));
            assert.ok(!result.diff.includes('secrets.json'));
            assert.ok(!result.diff.includes('credentials.json'));
            assert.ok(!result.diff.includes('backup-old'));
            assert.strictEqual(result.skipped, 5);
        } finally {
            await repo.cleanup();
        }
    });

    test('excludes binary files and invalid UTF-8 files', async () => {
        const repo = await createTestRepository({
            'src/binary.dat': Buffer.from([0x41, 0x00, 0x42]),
            'src/invalid.txt': Buffer.from([0xc3, 0x28]),
            'src/valid.ts': 'const valid = true;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.ok(result.diff.includes('+++ b/src/valid.ts'));
            assert.ok(!result.diff.includes('binary.dat'));
            assert.ok(!result.diff.includes('invalid.txt'));
            assert.strictEqual(result.skipped, 2);
        } finally {
            await repo.cleanup();
        }
    });

    test('excludes files larger than 32 KB', async () => {
        const repo = await createTestRepository({
            'src/large.txt': 'A'.repeat(32 * 1024 + 1),
            'src/small.txt': 'const small = true;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.ok(!result.diff.includes('large.txt'));
            assert.ok(result.diff.includes('+++ b/src/small.txt'));
            assert.strictEqual(result.skipped, 1);
        } finally {
            await repo.cleanup();
        }
    });

    test('respects the remaining diff character limit', async () => {
        const repo = await createTestRepository({
            'src/first.ts': 'const first = 1;\n',
            'src/second.ts': 'const second = 2;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                10
            );

            assert.strictEqual(result.diff, '');
            assert.strictEqual(result.skipped, 2);
        } finally {
            await repo.cleanup();
        }
    });

    test('returns no files when the remaining character limit is zero', async () => {
        const repo = await createTestRepository({
            'src/new.ts': 'const value = 1;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                0
            );

            assert.strictEqual(result.diff, '');
            assert.strictEqual(result.skipped, 0);
        } finally {
            await repo.cleanup();
        }
    });

    test('excludes private keys and generated directories', async () => {
        const repo = await createTestRepository({
            'certs/private.pem': 'PRIVATE KEY CONTENT\n',
            'certs/server.pfx': 'CERTIFICATE CONTENT\n',
            'node_modules/package/index.js': 'module.exports = {};\n',
            'dist/output.js': 'console.log("generated");\n',
            'src/app.ts': 'const app = true;\n'
        });

        try {
            const result = await buildUntrackedDiff(
                repo.root,
                repo.runGit,
                150_000
            );

            assert.ok(result.diff.includes('+++ b/src/app.ts'));
            assert.ok(!result.diff.includes('private.pem'));
            assert.ok(!result.diff.includes('server.pfx'));
            assert.ok(!result.diff.includes('node_modules'));
            assert.ok(!result.diff.includes('dist/output.js'));
            assert.strictEqual(result.skipped, 4);
        } finally {
            await repo.cleanup();
        }
    });
});
