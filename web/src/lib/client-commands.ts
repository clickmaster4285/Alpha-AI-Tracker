/**
 * Client commands the admin hands to the operator of an employee machine.
 *
 * "Remove Current Device" (server side) only revokes the device registration — the
 * machine keeps its local account (credentials, device token, collected rows) until
 * the desktop client's `--remove-account` command runs on it. That command wipes
 * every locally stored row and returns the tool to its sign-in page, which is what
 * completes the handover of a machine.
 *
 * Paths mirror what the build scripts install — keep them in sync when the install
 * layout changes (Installer-Parity Rule):
 *   Linux   — `client/publish/build-deb.sh` installs the binary at
 *             `/usr/share/$PACKAGE_NAME/client` and symlinks it as
 *             `/usr/bin/$PACKAGE_NAME` (PACKAGE_NAME = "alpha-ai-tracker", from
 *             `client/APP_IDENTIFIERS`), so the bare command works from any shell.
 *   Windows — `client/publish/installer-windows.iss` installs to
 *             `{autopf}\{#MyAppName}` = `C:\Program Files (x86)\Alpha AI Tracker`
 *             with `{#MyAppExeName}` = `client.exe`. Inno's `{autopf}` resolves to
 *             the 32-bit Program Files folder because the script does not set
 *             `ArchitecturesInstallIn64BitMode`.
 *
 * There is no server round-trip for these strings: they are packaging facts of the
 * desktop client, not workspace data.
 */
export interface RemoveAccountCommand {
  /** Install target the command belongs to. */
  os: 'Linux' | 'Windows';
  /** Shell label shown next to the command. */
  shell: string;
  /** Copy-paste-ready command (exactly what the operator runs). */
  command: string;
}

export const REMOVE_ACCOUNT_COMMANDS: RemoveAccountCommand[] = [
  {
    os: 'Linux',
    shell: 'Terminal',
    command: 'alpha-ai-tracker --remove-account',
  },
  {
    os: 'Windows',
    shell: 'PowerShell',
    // client.exe is a console subsystem binary so `& …` waits for completion
    // (spinner + success/fail). Add --no-pause to skip the final Enter prompt.
    command:
      '& "C:\\Program Files (x86)\\Alpha AI Tracker\\client.exe" --remove-account --no-pause',
  },
];

/** Windows DLP-protected uninstall (requires a one-time server-issued removal code). */
export function protectedUninstallCommand(code: string): string {
  // Double quotes work in both cmd.exe and PowerShell. Single quotes are LITERAL
  // in cmd (`--code 'XYZ'` sends the quote characters and verify returns 401).
  const safe = code.replace(/"/g, '');
  return `"C:\\Program Files (x86)\\Alpha AI Tracker\\client.exe" --protected-uninstall --code "${safe}" --no-pause`;
}
