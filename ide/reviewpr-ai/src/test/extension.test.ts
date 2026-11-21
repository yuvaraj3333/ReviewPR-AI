import * as assert from 'assert';
import {
    addedLinesByFile,
    safeRelativePath,
    filterAddedLineFindings
} from '../diffValidation';

const sampleDiff = [
    'diff --git a/src/app.ts b/src/app.ts',
    'index 1111111..2222222 100644',
    '--- a/src/app.ts',
    '+++ b/src/app.ts',
    '@@ -1,3 +1,4 @@',
    ' const first = 1;',
    '+const added = 2;',
    '-const removed = 3;',
    ' const last = 4;',
    'diff --git a/src/second file.ts b/src/second file.ts',
    '--- a/src/second file.ts',
    '+++ b/src/second file.ts',
    '@@ -1,2 +1,3 @@',
    ' const before = true;',
    '+const added = true;',
    ' const after = false;'
].join('\n');

function finding(overrides: Record<string, unknown> = {}) {
    return {
        filePath: 'src/app.ts',
        line: 2,
        severity: 'high',
        category: 'correctness',
        description: 'Potential defect in the added code.',
        suggestedFix: 'Correct the expression.',
        ...overrides
    };
}

suite('ReviewPR AI - Diff Validation', () => {
    test('accepts a finding on an added line', () => {
        const result = filterAddedLineFindings(sampleDiff, [finding()]);

        assert.strictEqual(result.accepted.length, 1);
        assert.strictEqual(result.rejected, 0);
    });

    test('rejects a finding on an unchanged line', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({ line: 1 })]
        );

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('rejects a finding on a deleted line', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({ line: 3 })]
        );

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('rejects a finding for a file not in the diff', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({ filePath: 'src/missing.ts' })]
        );

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('recognizes added lines in multiple files', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({
                filePath: 'src/second file.ts',
                line: 2
            })]
        );

        assert.strictEqual(result.accepted.length, 1);
    });

    test('parses added lines across multiple hunks', () => {
        const diff = [
            'diff --git a/src/app.ts b/src/app.ts',
            '--- a/src/app.ts',
            '+++ b/src/app.ts',
            '@@ -1,2 +1,3 @@',
            ' first',
            '+addedFirst',
            ' second',
            '@@ -10,2 +11,3 @@',
            ' tenth',
            '+addedSecond',
            ' eleventh'
        ].join('\n');

        const lines = addedLinesByFile(diff).get('src/app.ts');

        assert.ok(lines?.has(2));
        assert.ok(lines?.has(12));
        assert.ok(!lines?.has(1));
        assert.ok(!lines?.has(11));
    });

    test('rejects findings with invalid severity', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({ severity: 'urgent' })]
        );

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('rejects findings with an empty description', () => {
        const result = filterAddedLineFindings(
            sampleDiff,
            [finding({ description: '   ' })]
        );

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('rejects invalid and unsafe file paths', () => {
        const invalidPaths = [
            '../outside.ts',
            '/absolute.ts',
            'C:/outside.ts',
            'src/../outside.ts',
            'src\\outside.ts'
        ];

        for (const filePath of invalidPaths) {
            assert.strictEqual(
                safeRelativePath(filePath),
                undefined,
                `Expected path to be rejected: ${filePath}`
            );
        }
    });

    test('accepts a normal relative path containing spaces', () => {
        assert.strictEqual(
            safeRelativePath('src/second file.ts'),
            'src/second file.ts'
        );
    });

    test('rejects non-array provider output', () => {
        const result = filterAddedLineFindings(sampleDiff, null);

        assert.strictEqual(result.accepted.length, 0);
        assert.strictEqual(result.rejected, 1);
    });

    test('rejects findings beyond the 100-finding limit', () => {
        const candidates = Array.from(
            { length: 101 },
            () => finding()
        );

        const result = filterAddedLineFindings(sampleDiff, candidates);

        assert.strictEqual(result.accepted.length, 100);
        assert.strictEqual(result.rejected, 1);
    });
});
