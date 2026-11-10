/** Verify each AI finding against a Git added line before displaying a diagnostic. */
export interface ReviewFinding {
    filePath: string;
    line: number;
    severity: string;
    category: string;
    description: string;
    suggestedFix: string;
}

export function addedLinesByFile(diff: string): Map<string, Set<number>> {
    const result = new Map<string, Set<number>>();
    let currentFile = '';
    let newLine = 0;
    let inHunk = false;

    for (const line of diff.split(/\r?\n/)) {
        if (line.startsWith('diff --git ')) {
            currentFile = '';
            inHunk = false;
            continue;
        }
        if (line.startsWith('+++ b/')) {
            // Paths with Git's quotePath escaping need a proper parser; reject rather than guess.
            const name = line.substring(6);
            currentFile = safeRelativePath(name) ?? '';
            if (currentFile && !result.has(currentFile)) {
                result.set(currentFile, new Set<number>());
            }
            continue;
        }
        if (line.startsWith('+++ /dev/null')) {
            currentFile = '';
            inHunk = false;
            continue;
        }
        if (line.startsWith('@@ ')) {
            const match = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
            inHunk = !!match;
            if (match) {
                newLine = Number(match[1]);
            }
            continue;
        }
        if (!inHunk || !currentFile) {
            continue;
        }
        if (line.startsWith('+')) {
            result.get(currentFile)?.add(newLine);
            newLine++;
        } else if (line.startsWith(' ')) {
            newLine++;
        } else if (line.startsWith('-') || line.startsWith('\\')) {
            // Removed lines and '\ No newline at end of file' don't advance new-line positions.
        } else {
            inHunk = false;
        }
    }
    return result;
}

/** Strict relative Git path; reject traversal, drive names and ambiguous filenames. */
export function safeRelativePath(raw: string): string | undefined {
    if (typeof raw !== 'string' || raw.length < 1 || raw.length > 512 ||
        raw.startsWith('/') || raw.startsWith('\\') || raw.includes('\\') ||
        raw.includes(':') || raw.includes('"') || raw.includes('\0') ||
        raw.includes('\t') || raw.includes('\n')) {
        return undefined;
    }
    const parts = raw.split('/');
    if (parts.some(part => part === '' || part === '.' || part === '..')) {
        return undefined;
    }
    return raw;
}

export function filterAddedLineFindings(diff: string, candidates: unknown): {
    accepted: ReviewFinding[];
    rejected: number;
} {
    const allowed = addedLinesByFile(diff);
    const accepted: ReviewFinding[] = [];
    let rejected = 0;
    if (!Array.isArray(candidates)) {
        return { accepted, rejected: 1 };
    }
    for (const candidate of candidates.slice(0, 100)) {
        const f = candidate as Partial<ReviewFinding>;
        const file = typeof f?.filePath === 'string' ? safeRelativePath(f.filePath) : undefined;
        const severity = typeof f?.severity === 'string' ? f.severity.toLowerCase() : '';
        if (!file || typeof f.line !== 'number' || !Number.isSafeInteger(f.line) || !allowed.get(file)?.has(f.line) ||
            !['critical', 'high', 'medium', 'low', 'info', 'warning'].includes(severity) ||
            typeof f.description !== 'string' || f.description.trim().length === 0 ||
            f.description.length > 3000 || typeof f.suggestedFix !== 'string' ||
            f.suggestedFix.length > 3000 ||
            typeof f.category !== 'string' || f.category.length > 100) {
            rejected++;
            continue;
        }
        accepted.push({
            filePath: file,
            line: f.line,
            severity,
            category: f.category,
            description: f.description,
            suggestedFix: f.suggestedFix
        });
    }
    rejected += Math.max(0, candidates.length - 100);
    return { accepted, rejected };
}
