
import * as path from 'node:path';
import * as fs from 'node:fs/promises';
import { spawn, execFile } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import { promisify } from 'node:util';
import * as vscode from 'vscode';
import { analyzeMockDiff } from './mockReview';
import { filterAddedLineFindings, ReviewFinding } from './diffValidation';
import { buildUntrackedDiff } from './untrackedDiff';

const execFileAsync = promisify(execFile);

// Track active ReviewPR AI bridge processes for extension cleanup.
const activeBridgeProcesses = new Set<ChildProcess>();

const REVIEW_COMMAND = 'reviewpr-ai.reviewGitChanges';
const CONFIGURE_COMMAND = 'reviewpr-ai.configureProvider';

const MAX_DIFF_CHARS = 150_000;
const BRIDGE_TIMEOUT_MS = 120_000;
const MAX_BRIDGE_OUTPUT_CHARS = 10_000_000;

type Provider =
    | 'mock'
    | 'openai'
    | 'gemini'
    | 'anthropic'
    | 'groq'
    | 'openrouter';

interface BridgeResponse {
    success: boolean;
    error?: string;
    summary?: string;
    findings?: unknown;
    model?: string;
    models?: string[];
    truncated?: boolean;
    providerName?: string;
}

async function runGit(args: string[], cwd: string): Promise<string> {
    const { stdout } = await execFileAsync('git', args, {
        cwd,
        windowsHide: true,
        timeout: 30_000,
        maxBuffer: 8 * 1024 * 1024
    });

    return stdout.toString();
}

/**
 * Resolves the bridge executable from the installed extension directory.
 *
 * Expected packaged location:
 * extension/bin/win-x64/ReviewPR.IdeBridge.exe
 *
 * No user-configurable DLL or executable path is required.
 */
function getBridgeExecutable(
    context: vscode.ExtensionContext
): string {
    return path.join(
        context.extensionUri.fsPath,
        'bin',
        'win-x64',
        'ReviewPR.IdeBridge.exe'
    );
}

function isProvider(value: unknown): value is Provider {
    return value === 'mock' ||
        value === 'openai' ||
        value === 'gemini' ||
        value === 'anthropic' ||
        value === 'groq' ||
        value === 'openrouter';
}

async function configureProvider(
    context: vscode.ExtensionContext
): Promise<boolean> {

    const choice = await vscode.window.showQuickPick([
        {
            label: 'Mock — offline test',
            provider: 'mock' as Provider
        },
        {
            label: 'OpenAI',
            provider: 'openai' as Provider
        },
        {
            label: 'Google Gemini',
            provider: 'gemini' as Provider
        },
        {
            label: 'Anthropic Claude',
            provider: 'anthropic' as Provider
        },
        {
            label: 'Groq',
            provider: 'groq' as Provider
        },
        {
            label: 'OpenRouter',
            provider: 'openrouter' as Provider
        }
    ], {
        placeHolder: 'Choose your AI provider'
    });

    if (!choice) {
        return false;
    }

    const provider = choice.provider;

    // Offline mock provider requires no API key or bridge.
    if (provider === 'mock') {

        await context.globalState.update(
            'reviewpr.provider',
            'mock'
        );

        await context.globalState.update(
            'reviewpr.model.mock',
            'mock-positive-test'
        );

        vscode.window.showInformationMessage(
            'ReviewPR AI: Offline mock provider configured.'
        );

        return true;
    }

    const secretName = 'reviewpr.ai.key.' + provider;
    const existing = await context.secrets.get(secretName);

    const entered = await vscode.window.showInputBox({
        title: `ReviewPR AI — ${choice.label} API key`,

        prompt: existing
            ? 'Enter a new API key or leave blank to use your securely stored key.'
            : 'Enter your API key. It will be validated before being stored.',

        value: '',
        password: true,
        ignoreFocusOut: true,

        validateInput: value => {

            const candidate = value.trim() || existing || '';

            return candidate.length >= 8 &&
                candidate.length <= 4096 &&
                !/[\r\n]/.test(candidate)
                ? undefined
                : 'Enter a valid API key (8–4096 characters).';
        }
    });

    if (entered === undefined) {
        return false;
    }

    const keyToValidate = entered.trim() || existing || '';

    if (!keyToValidate) {

        vscode.window.showWarningMessage(
            'ReviewPR AI: An API key is required.'
        );

        return false;
    }

    try {

        const bridgeExe = getBridgeExecutable(context);

        const result = await callBridge(bridgeExe, {
            operation: 'configure',
            provider,
            apiKey: keyToValidate
        });

        if (
            !result.success ||
            !Array.isArray(result.models) ||
            result.models.length === 0 ||

            !result.models.every(
                model =>
                    typeof model === 'string' &&
                    /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$/.test(model)
            )
        ) {

            vscode.window.showErrorMessage(
                'ReviewPR AI: Provider validation or model discovery failed. ' +
                (result.error || 'No valid models were returned.')
            );

            return false;
        }

        const previousModel = context.globalState.get<string>(
            'reviewpr.model.' + provider
        );

        const modelChoice = await vscode.window.showQuickPick(
            result.models.map(model => ({
                label: model,
                description: model === previousModel
                    ? 'Previously selected'
                    : undefined
            })),
            {
                title: `ReviewPR AI — Select ${result.providerName || choice.label} model`,
                placeHolder: 'Choose the model to use for code reviews',
                matchOnDescription: true,
                ignoreFocusOut: true
            }
        );

        if (!modelChoice) {
            vscode.window.showInformationMessage(
                'ReviewPR AI: Model selection cancelled. Existing configuration was not changed.'
            );

            return false;
        }

        const selectedModel = modelChoice.label;

        // Store a newly entered key only after model selection.
        // VS Code SecretStorage protects the API key.
        if (entered.trim()) {

            await context.secrets.store(
                secretName,
                keyToValidate
            );
        }

        // Save the model independently for this provider.
        await context.globalState.update(
            'reviewpr.model.' + provider,
            selectedModel
        );

        await context.globalState.update(
            'reviewpr.provider',
            provider
        );

        vscode.window.showInformationMessage(
            `ReviewPR AI: ${result.providerName || choice.label} configured. ` +
            `Selected model: ${selectedModel}`
        );

        if (result.truncated) {
            vscode.window.showWarningMessage(
                'ReviewPR AI: The provider catalog exceeded the safety limit. ' +
                'Some models may not be displayed.'
            );
        }

        return true;

    } catch (error) {

        const message = error instanceof Error
            ? error.message
            : 'Provider configuration failed.';

        vscode.window.showErrorMessage(
            'ReviewPR AI: ' + message
        );

        return false;
    }
}
/**
 * Executes the packaged ReviewPR AI bridge.
 *
 * Security:
 * - API keys are never passed through command-line arguments.
 * - API keys are never passed through environment variables.
 * - API keys are never written to temporary files.
 * - API keys are sent to the bridge through stdin only.
 * - Raw stderr output is never displayed or logged.
 */
async function callBridge(
    executablePath: string,
    payload: object
): Promise<BridgeResponse> {

    // This packaged bridge currently targets Windows x64.
    if (
        process.platform !== 'win32' ||
        process.arch !== 'x64'
    ) {
        throw new Error(
            'AI providers currently require the Windows x64 ReviewPR AI bridge. ' +
            'The offline mock provider remains available.'
        );
    }

    if (
        !path.isAbsolute(executablePath) ||
        path.extname(executablePath).toLowerCase() !== '.exe'
    ) {
        throw new Error(
            'Invalid ReviewPR AI bridge executable path.'
        );
    }

    return new Promise<BridgeResponse>((resolve, reject) => {

        // Do not pass credentials through command-line arguments,
        // environment variables, logs or temporary files.
                const child = spawn(executablePath, [], {
            windowsHide: true,
            stdio: ['pipe', 'pipe', 'pipe']
        });

        // Register the process so it can be terminated during deactivation.
        activeBridgeProcesses.add(child);

        let stdout = '';
        let stderrSize = 0;
        let completed = false;

        const finish = (
            error?: Error,
            result?: BridgeResponse
        ) => {

            if (completed) {
                return;
            }

            completed = true;

            clearTimeout(timer);

            if (error) {
                reject(error);
            } else if (result) {
                resolve(result);
            } else {
                reject(
                    new Error(
                        'Local review bridge returned no response.'
                    )
                );
            }
        };

        const timer = setTimeout(() => {

            child.kill();

            finish(
                new Error(
                    'AI provider request timed out after 120 seconds.'
                )
            );

        }, BRIDGE_TIMEOUT_MS);

                child.on('error', () => {

            activeBridgeProcesses.delete(child);

            finish(
                new Error(
                    'Cannot start the ReviewPR AI bridge executable. ' +
                    'Reinstall the extension or check Windows security restrictions.'
                )
            );
        });

        child.stdout.setEncoding('utf8');

        child.stderr.on('data', (chunk: Buffer) => {

            // Never expose raw stderr because it could contain sensitive data.
            stderrSize += chunk.length;
        });

        child.stdout.on('data', (chunk: string) => {

            stdout += chunk;

            if (stdout.length > MAX_BRIDGE_OUTPUT_CHARS) {

                child.kill();

                finish(
                    new Error(
                        'AI review response exceeded the size limit.'
                    )
                );
            }
        });

        child.on('close', code => {

            activeBridgeProcesses.delete(child);

            if (completed) {
                return;
            }

            try {

                const result = JSON.parse(stdout) as BridgeResponse;

                if (typeof result?.success !== 'boolean') {
                    throw new Error('Invalid result.');
                }

                if (code !== 0 && result.success) {
                    throw new Error('Nonzero process exit.');
                }

                finish(undefined, result);

            } catch {

                finish(
                    new Error(
                        `Local review bridge failed (exit ${code}, diagnostic bytes ${stderrSize}).`
                    )
                );
            }
        });

        child.stdin.on('error', () => {
            // Child exit is reported by the close event.
        });

        // API key and request data are supplied through stdin only.
        child.stdin.end(JSON.stringify(payload));
    });
}

function severityOf(
    finding: ReviewFinding
): vscode.DiagnosticSeverity {

    switch (finding.severity) {

        case 'critical':
        case 'high':
            return vscode.DiagnosticSeverity.Error;

        case 'medium':
        case 'warning':
            return vscode.DiagnosticSeverity.Warning;

        case 'low':
        case 'info':
        default:
            return vscode.DiagnosticSeverity.Information;
    }
}

async function publishDiagnostics(
    collection: vscode.DiagnosticCollection,
    root: string,
    findings: ReviewFinding[]
): Promise<ReviewFinding[]> {

    collection.clear();

    const accepted: ReviewFinding[] = [];

    const realRoot = await fs.realpath(root);

    for (const finding of findings) {

        const absolute = path.resolve(
            root,
            ...finding.filePath.split('/')
        );

        try {

            const realFile = await fs.realpath(absolute);

            const rel = path.relative(realRoot, realFile);

            // Prevent diagnostics for symlinks that resolve outside this repository.
            if (
                !rel ||
                rel === '..' ||
                rel.startsWith('..' + path.sep) ||
                path.isAbsolute(rel)
            ) {
                continue;
            }

            const uri = vscode.Uri.file(realFile);

            const line = Math.max(0, finding.line - 1);

            const diagnostic = new vscode.Diagnostic(
                new vscode.Range(
                    new vscode.Position(line, 0),
                    new vscode.Position(line, 0)
                ),
                `${finding.description} Suggested fix: ${finding.suggestedFix}`,
                severityOf(finding)
            );

            diagnostic.source = 'ReviewPR AI';
            diagnostic.code = finding.category;

            collection.set(
                uri,
                [
                    ...(collection.get(uri) ?? []),
                    diagnostic
                ]
            );

            accepted.push(finding);

        } catch {

            // Ignore an unavailable file, not an arbitrary path supplied by an AI model.
        }
    }

    return accepted;
}

export function activate(context: vscode.ExtensionContext) {

    const output = vscode.window.createOutputChannel(
        'ReviewPR AI'
    );

    const diagnostics = vscode.languages.createDiagnosticCollection(
        'ReviewPR AI'
    );

    const configure = vscode.commands.registerCommand(
        CONFIGURE_COMMAND,
        async () => {
            await configureProvider(context);
        }
    );

    const review = vscode.commands.registerCommand(
        REVIEW_COMMAND,
        async () => {

            const folders = vscode.workspace.workspaceFolders?.filter(
                f => f.uri.scheme === 'file'
            ) ?? [];

            if (!folders.length) {

                vscode.window.showWarningMessage(
                    'ReviewPR AI: Open a local Git repository first.'
                );

                return;
            }

            const selected = folders.length === 1
                ? folders[0]
                : (
                    await vscode.window.showQuickPick(
                        folders.map(f => ({
                            label: f.name,
                            description: f.uri.fsPath,
                            folder: f
                        })),
                        {
                            placeHolder: 'Choose a Git repository'
                        }
                    )
                )?.folder;

            if (!selected) {
                return;
            }

            try {

                const root = (
                    await runGit(
                        ['rev-parse', '--show-toplevel'],
                        selected.uri.fsPath
                    )
                ).trim();

                const status = await runGit(
                    ['status', '--short', '--untracked-files=normal'],
                    root
                );

                const trackedDiff = await runGit(
                    [
                        '-c',
                        'core.quotepath=false',
                        'diff',
                        '--no-ext-diff',
                        '--no-textconv',
                        '--no-color',
                        '--unified=2',
                        'HEAD',
                        '--'
                    ],
                    root
                );

                const untracked = await buildUntrackedDiff(
                    root,
                    runGit,
                    Math.max(
                        0,
                        MAX_DIFF_CHARS - trackedDiff.length - (trackedDiff.length > 0 ? 1 : 0)
                    )
                );

                const separator = trackedDiff && untracked.diff ? '\n' : '';
                const diff = trackedDiff + separator + untracked.diff;

                output.appendLine(
    `DEBUG: Untracked diff contains SQL test: ${
        untracked.diff.includes('ReviewPR.SecurityTest.cs')
    }`
);

output.appendLine(
    `DEBUG: SQL injection code included: ${
        diff.includes("UserName = '" + '" + userName + "')
    }`
);

                output.clear();
                diagnostics.clear();

                output.appendLine(
                    'ReviewPR AI — Local Git Review'
                );

                output.appendLine(
                    `Repository: ${root}`
                );

                output.appendLine('');

                output.appendLine(
                    status.trim()
                        ? `Changed files:\n${status.trim()}`
                        : 'Git working tree clean.'
                );

                output.appendLine('');

                if (untracked.skipped > 0) {
                    output.appendLine(
                        `Untracked files skipped by safety filters or size limits: ${untracked.skipped}`
                    );
                    output.appendLine('');
                }

                if (!diff.trim()) {

                    output.appendLine(
                        'No tracked or eligible untracked Git changes to review.'
                    );

                    output.show(true);

                    return;
                }

                if (diff.length > MAX_DIFF_CHARS) {

                    output.appendLine(
                        'Diff exceeds 150,000 characters. Review smaller changes. Nothing was sent.'
                    );

                    output.show(true);

                    return;
                }

                let provider = context.globalState.get<string>(
                    'reviewpr.provider',
                    'mock'
                );

                if (!isProvider(provider)) {
                    provider = 'mock';
                }

                let model = context.globalState.get<string>(
                    'reviewpr.model.' + provider,
                    ''
                );

                if (provider === 'mock') {
                    model = 'mock-positive-test';
                }

                if (provider !== 'mock') {

                    const savedKey = await context.secrets.get(
                        'reviewpr.ai.key.' + provider
                    );

                    if (!model || !savedKey) {

                        const configured = await configureProvider(context);

                        if (!configured) {
                            return;
                        }

                        provider = context.globalState.get<string>(
                            'reviewpr.provider',
                            'mock'
                        );

                        if (!isProvider(provider)) {
                            return;
                        }

                        model = context.globalState.get<string>(
                            'reviewpr.model.' + provider,
                            ''
                        );

                        if (provider === 'mock') {
                            model = 'mock-positive-test';
                        }
                    }
                }

                let summary = '';
                let rawFindings: unknown[] = [];

                if (provider === 'mock') {

                    summary =
                        'Offline deterministic mock review (no external AI service).';

                    rawFindings = analyzeMockDiff(diff).map(f => ({
                        filePath: f.file,
                        line: f.line,
                        severity: f.severity,
                        category: 'mock',
                        description: f.description,
                        suggestedFix: f.suggestedFix
                    }));

                } else {

                    const key = await context.secrets.get(
                        'reviewpr.ai.key.' + provider
                    );

                    if (!key) {

                        vscode.window.showWarningMessage(
                            'ReviewPR AI: Set an API key using Configure Provider first.'
                        );

                        return;
                    }

                    const consent = await vscode.window.showWarningMessage(
                        `Send ${diff.length.toLocaleString()} characters of uncommitted Git diff ` +
                        `to ${provider} using model ${model}? Review the diff for secrets first.`,
                        { modal: true },
                        'Send for AI review'
                    );

                    if (consent !== 'Send for AI review') {
                        return;
                    }

                    // Resolve the packaged executable internally.
                    // No user-configurable bridge path is needed.
                    const bridgeExe = getBridgeExecutable(context);

                    const result = await callBridge(bridgeExe, {
                        operation: 'review',
                        provider,
                        model,
                        apiKey: key,
                        repositoryId: path.basename(root),
                        diff
                    });

                    if (!result.success) {

                        output.appendLine(
                            'AI request failed: ' +
                            (result.error || 'Unknown error.')
                        );

                        output.show(true);

                        return;
                    }

                    summary = typeof result.summary === 'string'
                        ? result.summary
                        : 'Review complete.';

                    rawFindings = Array.isArray(result.findings)
                        ? result.findings
                        : [];
                }

                const validated = filterAddedLineFindings(
                    diff,
                    rawFindings
                );

                const finalFindings = await publishDiagnostics(
                    diagnostics,
                    root,
                    validated.accepted
                );

                output.appendLine(
                    `Provider: ${provider}; Model: ${model}`
                );

                output.appendLine(
                    'Summary: ' + summary
                );

                output.appendLine('');

                for (const f of finalFindings) {

                    output.appendLine(
                        `[${f.severity.toUpperCase()}] ${f.filePath}:${f.line} (${f.category})`
                    );

                    output.appendLine(
                        'Issue: ' + f.description
                    );

                    output.appendLine(
                        'Suggested fix: ' + f.suggestedFix
                    );

                    output.appendLine('');
                }

                output.appendLine(
                    `Validated findings: ${finalFindings.length}`
                );

                const rejected =
                    validated.rejected +
                    validated.accepted.length -
                    finalFindings.length;

                if (rejected) {

                    output.appendLine(
                        `Rejected findings outside added diff lines or repository: ${rejected}`
                    );
                }

                output.appendLine(
                    'See View → Problems for clickable diagnostics.'
                );

                output.appendLine(
                    'Local tracked and eligible untracked changes only; remote PR approval is not implemented.'
                );

                output.show(true);

                vscode.window.showInformationMessage(
                    `ReviewPR AI: ${finalFindings.length} validated finding(s).`
                );

            } catch (error) {

                // Never render raw process input or error objects,
                // which might include credentials.
                const message = error instanceof Error
                    ? error.message
                    : 'Review failed.';

                output.appendLine(
                    'Review failed: ' + message
                );

                output.show(true);

                vscode.window.showErrorMessage(
                    'ReviewPR AI: Review failed. See the ReviewPR AI Output panel.'
                );
            }
        }
    );

    const button = vscode.window.createStatusBarItem(
        vscode.StatusBarAlignment.Left,
        100
    );

    button.text = '$(git-compare) ReviewPR AI';

    button.tooltip =
        'Review local Git changes with configured AI provider';

    button.command = REVIEW_COMMAND;

    button.show();

    context.subscriptions.push(
        review,
        configure,
        output,
        diagnostics,
        button
    );
}

export function deactivate(): void {

    // Terminate all active ReviewPR AI bridge processes.
    for (const child of activeBridgeProcesses) {

        try {
            if (child.exitCode === null && child.signalCode === null) {
                child.kill();
            }
        }
        catch {
            // Best-effort cleanup during extension shutdown.
        }
    }

    activeBridgeProcesses.clear();
}
