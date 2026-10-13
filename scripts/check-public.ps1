# Fails if the tree (and its git history, when there is one) carries anything that must not be public: FM9-Edit's
# dictionary (Fractal's data, which also lists the user's own preset names), a player's amp-level table, personal email
# addresses, user-folder paths, Azure details or the FracAI deployment. Run by CI on every push and before any release.
param(
	[string] $Root = (Resolve-Path "$PSScriptRoot/..").Path,
	[switch] $AllowAmpLevels
)

$ErrorActionPreference = 'Stop'
$problems = [System.Collections.Generic.List[string]]::new()

# Files that must never be in the tree, whatever they contain.
$forbiddenNames = @('*effect-definitions*.cache', 'effectDefinitions_*.cache', '*.pfx', '*.tfvars', '*.db', '*.db-wal', 'settings.local.json', 'http-token')
if (-not $AllowAmpLevels) { $forbiddenNames += 'fm9-12-amp-levels.json' }

$files = Get-ChildItem -Path $Root -Recurse -File -Force |
	Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules)[\\/]' }
foreach ($file in $files) {
	foreach ($pattern in $forbiddenNames) {
		if ($file.Name -like $pattern) { $problems.Add("forbidden file: $($file.FullName.Substring($Root.Length))") }
	}
}

# Text that must not appear. Patterns are generic on purpose: the scan itself must not contain anyone's details.
$forbiddenText = [ordered]@{
	'personal email address'   = '[A-Za-z0-9._%+-]+@(gmail|googlemail|outlook|hotmail|live|yahoo|icloud|me|proton|protonmail)\.(com|co\.uk|me)'
	'Windows user folder'      = '[A-Za-z]:\\+Users\\+[A-Za-z]'
	'OneDrive folder'          = 'OneDrive[\\/]'
	'Azure resource or secret' = '(rg-fracai|\.openai\.azure\.com|\.blob\.core\.windows\.net|AccountKey=|SharedAccessSignature=|/subscriptions/[0-9a-fA-F-]{36})'
	'FracAI deployment'        = '(greymansoftware\.com|fracai-ops|deploy-azure)'
	'private key'              = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
}
$textExtensions = @('.cs', '.csproj', '.slnx', '.props', '.json', '.md', '.ts', '.ps1', '.py', '.yml', '.yaml', '.txt', '.xml', '.config', '.gitignore', '.editorconfig')
foreach ($file in $files) {
	if ($textExtensions -notcontains $file.Extension -and $file.Name -notin @('.gitignore', 'LICENSE', 'NOTICE')) { continue }
	if ($file.FullName -eq $PSCommandPath) { continue }
	$text = Get-Content -LiteralPath $file.FullName -Raw
	if ($null -eq $text) { continue }
	foreach ($rule in $forbiddenText.GetEnumerator()) {
		foreach ($match in [regex]::Matches($text, $rule.Value)) {
			$problems.Add("$($rule.Key) in $($file.FullName.Substring($Root.Length)): '$($match.Value)'")
		}
	}
}

# Every commit author and committer must use a no-reply address.
if (Test-Path (Join-Path $Root '.git')) {
	$emails = git -C $Root log --all --format='%ae%n%ce' | Sort-Object -Unique
	foreach ($email in $emails) {
		if ($email -and $email -notmatch '@users\.noreply\.github\.com$' -and $email -ne 'noreply@anthropic.com') {
			$problems.Add("commit email that is not a no-reply address: $email")
		}
	}
}

if ($problems.Count -gt 0) {
	$problems | ForEach-Object { Write-Host "BLOCKED: $_" }
	exit 1
}
Write-Host "Public check passed: $($files.Count) files scanned."
