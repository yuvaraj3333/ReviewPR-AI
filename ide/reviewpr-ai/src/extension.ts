import * as vscode from 'vscode';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

async function runGit(args: string[], cwd: string): Promise<string> {
    const { stdout } = await execFileAsync('git', args, {
        cwd,
        windowsHide: true,
        timeout: 30000,
        maxBuffer: 8 * 1024 * 1024
    });
    return stdout.toString();
}

export function activate(context: vscode.ExtensionContext) {
    const output = vscode.window.createOutputChannel('ReviewPR AI');
    const commandId = 'reviewpr-ai.reviewGitChanges';

    const command = vscode.commands.registerCommand(commandId, async () => {
        const folders = vscode.workspace.workspaceFolders?.filter(
            folder => folder.uri.scheme === 'file'
        ) ?? [];

        if (folders.length === 0) {
            vscode.window.showWarningMessage(
                'ReviewPR AI: Open a local Git repository first.'
            );
            return;
        }

        let selected = folders[0];

        if (folders.length > 1) {
            const choice = await vscode.window.showQuickPick(
                folders.map(folder => ({
                    label: folder.name,
                    description: folder.uri.fsPath,
                    folder
                })),
                { placeHolder: 'Select a repository to review' }
            );

            if (!choice) {
                return;
            }

            selected = choice.folder;
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

            const diff = await runGit(
                [
                    'diff',
                    '--no-ext-diff',
                    '--no-textconv',
                    '--unified=2',
                    'HEAD',
                    '--'
                ],
                root
            );

            output.clear();
            output.appendLine('ReviewPR AI - Git Change Review');
            output.appendLine('================================');
            output.appendLine(`Repository: ${root}`);
            output.appendLine('');

            if (status.trim()) {
                output.appendLine('Changed files:');
                output.appendLine(status.trim());
            } else {
                output.appendLine('Git working tree is clean.');
            }

            output.appendLine('');
            output.appendLine('Tracked Git diff:');
            output.appendLine('--------------------------------');

            if (diff.trim()) {
                const limit = 150000;
                output.appendLine(diff.slice(0, limit));

                if (diff.length > limit) {
                    output.appendLine(
                        '\n[Diff truncated for display]'
                    );
                }
            } else {
                output.appendLine(
                    'No tracked uncommitted changes to review.'
                );
            }

            output.appendLine('');
            output.appendLine(
                'AI analysis is not connected yet.'
            );
            output.appendLine(
                'Untracked files are listed but not included in the diff.'
            );

            output.show(true);

            vscode.window.showInformationMessage(
                'ReviewPR AI: Git changes collected.'
            );
        } catch (error) {
            const message = error instanceof Error
                ? error.message
                : String(error);

            vscode.window.showErrorMessage(
                `ReviewPR AI: ${message}`
            );
        }
    });

    const statusButton = vscode.window.createStatusBarItem(
        vscode.StatusBarAlignment.Left,
        100
    );

    statusButton.text = '$(git-compare) ReviewPR AI';
    statusButton.tooltip = 'Review Git changes (AI integration pending)';
    statusButton.command = commandId;
    statusButton.show();

    context.subscriptions.push(command, output, statusButton);
}

export function deactivate() {}
