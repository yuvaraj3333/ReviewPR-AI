/** Deterministic, OFFLINE mock reviewer; it does not call an AI service. */
export interface MockFinding {
    file: string;
    line: number;
    severity: 'warning' | 'info';
    description: string;
    suggestedFix: string;
}

export function analyzeMockDiff(diff: string): MockFinding[] {
    const findings: MockFinding[] = [];
    let file = '';
    let newLine = 0;
    let inHunk = false;

    for (const raw of diff.split(/\r?\n/)) {
        if (raw.startsWith('diff --git ')) {
            file = '';
            inHunk = false;
            continue;
        }
        if (raw.startsWith('+++ b/')) {
            file = raw.substring('+++ b/'.length);
            continue;
        }
        if (raw.startsWith('+++ /dev/null')) {
            file = '';
            continue;
        }
        if (raw.startsWith('@@ ')) {
            const match = raw.match(/\+(\d+)(?:,\d+)?\s+@@/);
            inHunk = !!match;
            if (match) {
                newLine = Number(match[1]);
            }
            continue;
        }
        if (!inHunk || !file) {
            continue;
        }
        if (raw.startsWith('+') && !raw.startsWith('+++')) {
            const content = raw.substring(1);
            let finding: Omit<MockFinding, 'file' | 'line'> | undefined;
            if (content.includes('ReviewPR AI test change')) {
                finding = {
                    severity: 'info',
                    description: 'Mock smoke-test finding on your README test marker (not an AI-generated issue).',
                    suggestedFix: 'Remove the temporary test marker after verifying the integration.'
                };
            } else if (/\beval\s*\(/.test(content)) {
                finding = {
                    severity: 'warning',
                    description: 'Mock rule: dynamic eval call requires security review.',
                    suggestedFix: 'Avoid eval; use a parser or explicit dispatch where appropriate.'
                };
            } else if (/\b(?:SELECT|UPDATE|INSERT|DELETE)\b.*\+/.test(content.toUpperCase())) {
                finding = {
                    severity: 'warning',
                    description: 'Mock rule: possible SQL query string concatenation.',
                    suggestedFix: 'Use parameterized queries instead of concatenating input into SQL.'
                };
            }
            if (finding) {
                findings.push({ file, line: newLine, ...finding });
            }
            newLine++;
        } else if (raw.startsWith(' ')) {
            newLine++;
        }
    }
    return findings;
}
