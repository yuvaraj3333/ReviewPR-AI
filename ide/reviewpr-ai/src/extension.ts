import * as path from 'node:path';
import * as fs from 'node:fs/promises';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import * as vscode from 'vscode';
import { analyzeMockDiff } from './mockReview';
import { filterAddedLineFindings, ReviewFinding } from './diffValidation';

const execFileAsync = promisify(execFile);
const REVIEW_COMMAND = 'reviewpr-ai.reviewGitChanges';
const CONFIGURE_COMMAND = 'reviewpr-ai.configureProvider';
const MAX_DIFF_CHARS = 150_000;

type Provider = 'mock' | 'openai' | 'gemini';
interface BridgeResponse {
    success: boolean;
    error?: string;
    summary?: string;
    findings?: unknown;
}

async function runGit(args: string[], cwd: string): Promise<string> {
    const { stdout } = await execFileAsync('git', args, {
        cwd, windowsHide: true, timeout: 30_000, maxBuffer: 8 * 1024 * 1024
    });
    return stdout.toString();
}

function isProvider(value: unknown): value is Provider {
    return value === 'mock' || value === 'openai' || value === 'gemini';
}

async function configureProvider(context: vscode.ExtensionContext): Promise<boolean> {
    const choice = await vscode.window.showQuickPick([
        { label: 'Mock — offline test', provider: 'mock' as Provider },
        { label: 'OpenAI — real API request', provider: 'openai' as Provider },
        { label: 'Google Gemini — real API request', provider: 'gemini' as Provider }
    ], { placeHolder: 'Choose the AI provider for ReviewPR AI' });
    if (!choice) { return false; }
    const provider = choice.provider;
    let model = 'mock-positive-test';
    if (provider !== 'mock') {
        const savedModel = context.globalState.get<string>('reviewpr.model.' + provider, '');
        const input = await vscode.window.showInputBox({
            title: `ReviewPR AI — ${provider} model`,
            prompt: 'Enter the exact model ID supported by your provider account',
            value: savedModel,
            placeHolder: provider === 'openai' ? 'Example: gpt-5-mini' : 'Example: gemini-2.5-flash',
            validateInput: value => /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$/.test(value)
                ? undefined : 'Enter a valid model identifier (1–100 characters).'
        });
        if (input === undefined) { return false; }
        model = input.trim();
        const existing = await context.secrets.get('reviewpr.ai.key.' + provider);
        const apiKey = await vscode.window.showInputBox({
            title: `ReviewPR AI — ${provider} API key`,
            prompt: existing ? 'Leave blank to retain your securely stored key.' : 'Enter your provider API key (stored in VS Code SecretStorage).',
            password: true,
            ignoreFocusOut: true,
            validateInput: value => !value && existing ? undefined
                : value.length >= 8 && value.length <= 4096 && !/[\r\n]/.test(value) ? undefined
                : 'Enter an API key of 8–4096 characters.'
        });
        if (apiKey === undefined) { return false; }
        if (apiKey) { await context.secrets.store('reviewpr.ai.key.' + provider, apiKey); }
    }
    await context.globalState.update('reviewpr.provider', provider);
    await context.globalState.update('reviewpr.model.' + provider, model);
    vscode.window.showInformationMessage(`ReviewPR AI: ${provider} configured (model: ${model}).`);
    return true;
}

async function callBridge(dllPath: string, payload: object): Promise<BridgeResponse> {
    if (!path.isAbsolute(dllPath) || path.extname(dllPath).toLowerCase() !== '.dll') {
        throw new Error('Set reviewpr-ai.bridgeDllPath to the absolute path of ReviewPR.IdeBridge.dll.');
    }
    try { await fs.access(dllPath); }
    catch { throw new Error('Local .NET review bridge not found. Build ReviewPR.IdeBridge in Release first.'); }

    return new Promise<BridgeResponse>((resolve, reject) => {
        // Do not pass credentials via command-line arguments, environment, logs or temp files.
        const child = spawn('dotnet', [dllPath], {
            windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']
        });
        let stdout = '';
        let stderrSize = 0;
        let completed = false;
        const timer = setTimeout(() => child.kill(), 120_000);
        const finish = (error?: Error, result?: BridgeResponse) => {
            if (completed) { return; }
            completed = true;
            clearTimeout(timer);
            if (error) { reject(error); }
            else if (result) { resolve(result); }
            else { reject(new Error('Local review bridge returned no response.')); }
        };
        child.on('error', () => finish(new Error('Cannot start dotnet. Install the .NET 10 runtime and check PATH.')));
        child.stdout.setEncoding('utf8');
        child.stderr.on('data', (chunk: Buffer) => {
            stderrSize += chunk.length; // Never surface raw stderr; it could include secrets.
        });
        child.stdout.on('data', (chunk: string) => {
            stdout += chunk;
            if (stdout.length > 1_000_000) {
                child.kill();
                finish(new Error('AI review response exceeded the size limit.'));
            }
        });
        child.on('close', code => {
            if (completed) { return; }
            try {
                const result = JSON.parse(stdout) as BridgeResponse;
                if (typeof result?.success !== 'boolean') { throw new Error('Invalid result.'); }
                if (code !== 0 && result.success) { throw new Error('Nonzero process exit.'); }
                finish(undefined, result);
            } catch {
                finish(new Error(`Local review bridge failed (exit ${code}, diagnostic bytes ${stderrSize}).`));
            }
        });
        child.stdin.on('error', () => { /* Child exit is reported by close. */ });
        child.stdin.end(JSON.stringify(payload));
    });
}

function severityOf(finding: ReviewFinding): vscode.DiagnosticSeverity {
    switch (finding.severity) {
        case 'critical': case 'high': return vscode.DiagnosticSeverity.Error;
        case 'medium': case 'warning': return vscode.DiagnosticSeverity.Warning;
        case 'low': case 'info': default: return vscode.DiagnosticSeverity.Information;
    }
}

async function publishDiagnostics(
    collection: vscode.DiagnosticCollection, root: string, findings: ReviewFinding[]
): Promise<ReviewFinding[]> {
    collection.clear();
    const accepted: ReviewFinding[] = [];
    const realRoot = await fs.realpath(root);
    for (const finding of findings) {
        const absolute = path.resolve(root, ...finding.filePath.split('/'));
        try {
            const realFile = await fs.realpath(absolute);
            const rel = path.relative(realRoot, realFile);
            // Prevent diagnostics for symlinks that resolve outside this repository.
            if (!rel || rel === '..' || rel.startsWith('..' + path.sep) || path.isAbsolute(rel)) { continue; }
            const uri = vscode.Uri.file(realFile);
            const line = Math.max(0, finding.line - 1);
            const diagnostic = new vscode.Diagnostic(
                new vscode.Range(new vscode.Position(line, 0), new vscode.Position(line, 0)),
                `${finding.description} Suggested fix: ${finding.suggestedFix}`,
                severityOf(finding)
            );
            diagnostic.source = 'ReviewPR AI';
            diagnostic.code = finding.category;
            collection.set(uri, [...(collection.get(uri) ?? []), diagnostic]);
            accepted.push(finding);
        } catch {
            // Ignore an unavailable file, not an arbitrary path supplied by an AI model.
        }
    }
    return accepted;
}

export function activate(context: vscode.ExtensionContext) {
    const output = vscode.window.createOutputChannel('ReviewPR AI');
    const diagnostics = vscode.languages.createDiagnosticCollection('ReviewPR AI');

    const configure = vscode.commands.registerCommand(CONFIGURE_COMMAND,
        async () => { await configureProvider(context); });

    const review = vscode.commands.registerCommand(REVIEW_COMMAND, async () => {
        const folders = vscode.workspace.workspaceFolders?.filter(f => f.uri.scheme === 'file') ?? [];
        if (!folders.length) {
            vscode.window.showWarningMessage('ReviewPR AI: Open a local Git repository first.');
            return;
        }
        const selected = folders.length === 1 ? folders[0] :
            (await vscode.window.showQuickPick(folders.map(f => ({
                label: f.name, description: f.uri.fsPath, folder: f
            })), { placeHolder: 'Choose a Git repository' }))?.folder;
        if (!selected) { return; }

        try {
            const root = (await runGit(['rev-parse', '--show-toplevel'], selected.uri.fsPath)).trim();
            const status = await runGit(['status', '--short', '--untracked-files=normal'], root);
            const diff = await runGit([
                '-c', 'core.quotepath=false', 'diff', '--no-ext-diff', '--no-textconv',
                '--no-color', '--unified=2', 'HEAD', '--'
            ], root);

            output.clear();
            diagnostics.clear();
            output.appendLine('ReviewPR AI — Local Git Review');
            output.appendLine(`Repository: ${root}`);
            output.appendLine('');
            output.appendLine(status.trim() ? `Changed files:\n${status.trim()}` : 'Git working tree clean.');
            output.appendLine('');
            if (!diff.trim()) {
                output.appendLine('No tracked uncommitted Git changes. Untracked files are excluded.');
                output.show(true);
                return;
            }
            if (diff.length > MAX_DIFF_CHARS) {
                output.appendLine('Diff exceeds 150,000 characters. Review smaller changes. Nothing was sent.');
                output.show(true);
                return;
            }

            let provider = context.globalState.get<string>('reviewpr.provider', 'mock');
            if (!isProvider(provider)) { provider = 'mock'; }
            let model = context.globalState.get<string>('reviewpr.model.' + provider, '');
            if (provider === 'mock') { model = 'mock-positive-test'; }
            if (provider !== 'mock' && !model) {
                const configured = await configureProvider(context);
                if (!configured) { return; }
                provider = context.globalState.get<string>('reviewpr.provider', 'mock');
                if (!isProvider(provider)) { return; }
                model = context.globalState.get<string>('reviewpr.model.' + provider, '');
            }

            let summary = '';
            let rawFindings: unknown[] = [];
            if (provider === 'mock') {
                summary = 'Offline deterministic mock review (no external AI service).';
                rawFindings = analyzeMockDiff(diff).map(f => ({
                    filePath: f.file, line: f.line, severity: f.severity, category: 'mock',
                    description: f.description, suggestedFix: f.suggestedFix
                }));
            } else {
                const key = await context.secrets.get('reviewpr.ai.key.' + provider);
                if (!key) {
                    vscode.window.showWarningMessage('ReviewPR AI: Set an API key using Configure Provider first.');
                    return;
                }
                const consent = await vscode.window.showWarningMessage(
                    `Send ${diff.length.toLocaleString()} characters of uncommitted Git diff ` +
                    `to ${provider} using model ${model}? Review the diff for secrets first.`,
                    { modal: true }, 'Send for AI review'
                );
                if (consent !== 'Send for AI review') { return; }

                const dll = vscode.workspace.getConfiguration('reviewpr-ai')
                    .get<string>('bridgeDllPath', '').trim();
                const result = await callBridge(dll, {
                    provider, model, apiKey: key, repositoryId: path.basename(root), diff
                });
                if (!result.success) {
                    output.appendLine('AI request failed: ' + (result.error || 'Unknown error.'));
                    output.show(true);
                    return;
                }
                summary = typeof result.summary === 'string' ? result.summary : 'Review complete.';
                rawFindings = Array.isArray(result.findings) ? result.findings : [];
            }

            const validated = filterAddedLineFindings(diff, rawFindings);
            const finalFindings = await publishDiagnostics(diagnostics, root, validated.accepted);
            output.appendLine(`Provider: ${provider}; Model: ${model}`);
            output.appendLine('Summary: ' + summary);
            output.appendLine('');
            for (const f of finalFindings) {
                output.appendLine(`[${f.severity.toUpperCase()}] ${f.filePath}:${f.line} (${f.category})`);
                output.appendLine('Issue: ' + f.description);
                output.appendLine('Suggested fix: ' + f.suggestedFix);
                output.appendLine('');
            }
            output.appendLine(`Validated findings: ${finalFindings.length}`);
            const rejected = validated.rejected + validated.accepted.length - finalFindings.length;
            if (rejected) {
                output.appendLine(`Rejected findings outside added diff lines or repository: ${rejected}`);
            }
            output.appendLine('See View → Problems for clickable diagnostics.');
            output.appendLine('Local tracked changes only; remote PR approval is not implemented.');
            output.show(true);
            vscode.window.showInformationMessage(`ReviewPR AI: ${finalFindings.length} validated finding(s).`);
        } catch (error) {
            // Never render raw process input or error objects, which might include credentials.
            const message = error instanceof Error ? error.message : 'Review failed.';
            output.appendLine('Review failed: ' + message);
            output.show(true);
            vscode.window.showErrorMessage('ReviewPR AI: Review failed. See the ReviewPR AI Output panel.');
        }
    });

    const button = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    button.text = '$(git-compare) ReviewPR AI';
    button.tooltip = 'Review local Git changes with configured AI provider';
    button.command = REVIEW_COMMAND;
    button.show();
    context.subscriptions.push(review, configure, output, diagnostics, button);
}

export function deactivate() {}
