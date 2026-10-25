# Self-check: builds a known tree, then verifies scan totals, stats, search and duplicate detection.
$root = Join-Path $env:TEMP ("spacescan_check_" + [guid]::NewGuid())
$deep = $root + ('\' + ('d' * 50)) * 6   # ~320 chars, past MAX_PATH
[IO.Directory]::CreateDirectory("\\?\$deep") | Out-Null
[IO.File]::WriteAllBytes("\\?\$root\a.bin", (New-Object byte[] 1000))
[IO.File]::WriteAllBytes("\\?\$deep\b.bin", (New-Object byte[] 2345))
# Two identical files in different folders, plus a same-sized file with different content.
$dupe = [byte[]](1..5000 | ForEach-Object { $_ % 251 })
$other = [byte[]](1..5000 | ForEach-Object { ($_ * 7) % 251 })
[IO.File]::WriteAllBytes("\\?\$root\copy1.bin", $dupe)
[IO.File]::WriteAllBytes("\\?\$deep\copy2.bin", $dupe)
[IO.File]::WriteAllBytes("\\?\$root\notcopy.bin", $other)

$asm = [Reflection.Assembly]::LoadFile("$PSScriptRoot\SpaceScan.exe")
$node = [Activator]::CreateInstance($asm.GetType('Node'))
$node.GetType().GetField('Name').SetValue($node, $root)
$asm.GetType('Scanner').GetMethod('Scan').Invoke($null, [object[]]@($node, [string]$root)) | Out-Null
$size = $node.GetType().GetField('Size').GetValue($node)
$files = $node.GetType().GetField('Files').GetValue($node)
"Scan: $files files, $size bytes (expected 5 files, 18345 bytes)"
if ($size -ne 18345 -or $files -ne 5) { throw 'SCAN MISMATCH' } else { 'SCAN OK' }

# Top-files and extension stats from the same scan.
$stats = $asm.GetType('Stats')
$stats.GetMethod('Trim').Invoke($null, $null) | Out-Null
$top = $stats.GetField('Top').GetValue($null)
$ext = $stats.GetField('Ext').GetValue($null)
"Stats: biggest=$($top[0].Name) $($top[0].Size); .bin=$($ext['.bin'][0]) bytes / $($ext['.bin'][1]) files"
if ($top[0].Size -ne 5000 -or $ext['.bin'][0] -ne 18345 -or $ext['.bin'][1] -ne 5) { throw 'STATS MISMATCH' } else { 'STATS OK' }

# Search: name filter plus a size floor, over the scanned tree (no MFT for a plain folder).
$q = [Activator]::CreateInstance($asm.GetType('Query'))
$q.Name = 'copy'; $q.MinSize = 100
$hits = $asm.GetType('Finder').GetMethod('Run').Invoke($null, [object[]]@($node, $q))
"Search 'copy' >=100B: $($hits.Count) hits ($(($hits | ForEach-Object Name) -join ', '))"
if ($hits.Count -ne 3) { throw 'SEARCH MISMATCH' } else { 'SEARCH OK' }   # copy1, copy2, notcopy

# Duplicates: only the two identical files, not the same-sized one with other content.
$all = [Activator]::CreateInstance($asm.GetType('Query'))
$groups = $asm.GetType('Dupes').GetMethod('Find').Invoke($null, [object[]]@($hits, [Action[long, long]] { }))
"Duplicates: $($groups.Count) group(s), $($groups[0].Count) copies of $($groups[0][0].Name)"
if ($groups.Count -ne 1 -or $groups[0].Count -ne 2 -or $groups[0][0].Size -ne 5000) { throw 'DUPES MISMATCH' } else { 'DUPES OK' }

# Exclusions: a name rule drops the files, a path rule drops a whole subtree.
$exclude = $asm.GetType('Exclude')
function Scan-Fresh { $n = [Activator]::CreateInstance($asm.GetType('Node')); $n.Name = $root
    $asm.GetType('Scanner').GetMethod('Scan').Invoke($null, [object[]]@($n, [string]$root)) | Out-Null; $n }
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@('*.bin')))
$none = Scan-Fresh
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@("$root\$('d' * 50)")))
$noSub = Scan-Fresh
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@("$root\copy1.bin")))
$noFile = Scan-Fresh
# path rules with a wildcard, as used for files during an MFT scan
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@('C:\Temp\*.iso')))
$leaf = $exclude.GetMethod('LeafMatch').Invoke($null, [object[]]@('disk.iso'))
$leafNo = $exclude.GetMethod('LeafMatch').Invoke($null, [object[]]@('disk.zip'))
$inPath = $exclude.GetMethod('MatchPath').Invoke($null, [object[]]@('C:\Temp\disk.iso'))
$outPath = $exclude.GetMethod('MatchPath').Invoke($null, [object[]]@('C:\Other\disk.iso'))
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@()))
"Path rules: single file -> $($noFile.Size) bytes (expect 13345); leaf disk.iso=$leaf disk.zip=$leafNo; C:\Temp\disk.iso=$inPath C:\Other\disk.iso=$outPath"
if ($noFile.Size -ne 13345 -or -not $leaf -or $leafNo -or -not $inPath -or $outPath) { throw 'PATH RULE MISMATCH' } else { 'PATH RULE OK' }
"Exclusions: '*.bin' -> $($none.Size) bytes (expect 0); '<root>\<deep>' -> $($noSub.Size) bytes (expect 11000, only the files in the root)"
if ($none.Size -ne 0 -or $noSub.Size -ne 11000) { throw 'EXCLUDE MISMATCH' } else { 'EXCLUDE OK' }

# Snapshot + compare: save, grow a file, compare.
$snap = Join-Path $env:TEMP 'spacescan_check.scan'
$asm.GetType('Snapshot').GetMethod('Save').Invoke($null, [object[]]@($node, [string]$snap))
[IO.File]::WriteAllBytes("\\?\$root\grow.bin", (New-Object byte[] 4000))
$after = Scan-Fresh
$loadArgs = [object[]]@([string]$snap, $null, $null)
$before = $asm.GetType('Snapshot').GetMethod('Load').Invoke($null, $loadArgs)
$diff = $asm.GetType('Comparer').GetMethod('Run').Invoke($null, [object[]]@($before, $after, 100))
"Compare: snapshot of $($loadArgs[2]) -> $($diff.Count) changed folder(s), root delta $($diff[0].Delta) (expect 1 and +4000)"
if ($before.Size -ne 18345 -or $diff.Count -ne 1 -or $diff[0].Delta -ne 4000) { throw 'COMPARE MISMATCH' } else { 'COMPARE OK' }
[IO.File]::Delete($snap)

# Ecosystem: the card, garbage read as absent, Shelf's game folders named only while Shelf is installed.
$eco = $asm.GetType('Ecosystem')
$ecoDir = "$root\eco"
$eco.GetField('Folder', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $ecoDir)
$eco.GetMethod('WriteCard', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@("$PSScriptRoot\SpaceScan.exe", '1.1.0'))
$cardBytes = [IO.File]::ReadAllBytes("$ecoDir\apps\spacescan.json")
$card = [Text.Encoding]::UTF8.GetString($cardBytes) | ConvertFrom-Json
$exeOf = { param($id) $eco.GetMethod('Exe', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@($id)) }
$gameAt = { param($path) $n = [Activator]::CreateInstance($asm.GetType('Node')); $n.Name = $path
    $eco.GetMethod('Forget', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $null) | Out-Null
    $eco.GetMethod('GameAt', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@($n)) }
$bought = { $eco.GetMethod('BoughtSomething', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $null) }
"Card: schema $($card.schema), $($card.id) $($card.version), BOM: $($cardBytes[0] -eq 0xEF)"
if ($card.schema -ne 1 -or $card.version -ne '1.1.0' -or $cardBytes[0] -eq 0xEF -or (& $exeOf 'spacescan') -ne "$PSScriptRoot\SpaceScan.exe") { throw 'CARD MISMATCH' } else { 'CARD OK' }
$utf8 = New-Object Text.UTF8Encoding($false)
$self = ConvertTo-Json "$PSScriptRoot\SpaceScan.exe"
$jroot = $root -replace '\\', '\\'
$games = '{"schema":1,"games":[{"id":"g1","name":"Big Game","installDir":"' + $jroot + '\\Big Game\\","canMove":true},{"id":"g2","name":"Stuck","installDir":"' + $jroot + '\\Stuck"},{"id":"g3","name":"Drive","installDir":"D:\\"}]}'
[IO.Directory]::CreateDirectory("$ecoDir\data") | Out-Null
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", $games, $utf8)
$noShelf = & $gameAt "$root\Big Game"
[IO.File]::WriteAllText("$ecoDir\apps\shelf.json", '{"schema":1,"paid":true,"edition":"trial","exe":' + $self + '}', $utf8)
$big = & $gameAt "$root\big game"; $stuck = & $gameAt "$root\Stuck"; $drive = & $gameAt 'D:\'
$trialBought = & $bought
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", '{ broken', $utf8)
$broken = & $gameAt "$root\Big Game"
[IO.File]::WriteAllText("$ecoDir\apps\shelf.json", '{"schema":1,"paid":true,"edition":"full","exe":' + $self + '}', $utf8)
[IO.File]::WriteAllText("$ecoDir\apps\reel.json", '{"schema":1,"paid":true,"edition":"full","exe":"C:\\gone\\Reel.exe"}', $utf8)
$reelGone = & $exeOf 'reel'
"Shelf games: without Shelf=$noShelf; '$($big.Name)' canMove=$($big.CanMove); '$($stuck.Name)' canMove=$($stuck.CanMove); a drive=$drive; broken file=$broken; trial bought=$trialBought, full bought=$(& $bought); uninstalled Reel=$reelGone"
if ($noShelf -or $big.Id -ne 'g1' -or -not $big.CanMove -or $stuck.CanMove -or $drive -or $broken -or $trialBought -or -not (& $bought) -or $reelGone) { throw 'ECOSYSTEM MISMATCH' } else { 'ECOSYSTEM OK' }

# Move this game (Shelf): the command line SpaceScan hands Shelf, only for a movable game while Shelf is installed;
# anything hostile or malformed in shelf.games.json or in Shelf's card is read as "not there".
$move = { param($path) $n = [Activator]::CreateInstance($asm.GetType('Node')); $n.Name = $path
    $eco.GetMethod('Forget', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $null) | Out-Null
    $eco.GetMethod('MoveCommand', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@($n)) }
$uuid = '0f8fad5b-d9cb-469f-a165-70867728950e'
$long = 'x' * 5000
$hostile = '{"schema":1,"games":[' +
    '{"id":"' + $uuid + '","name":"Mover","installDir":"' + $jroot + '\\Mover","canMove":true},' +
    '{"id":"store1","name":"Store","installDir":"' + $jroot + '\\Store","canMove":false},' +
    '{"id":"str","name":"Str","installDir":"' + $jroot + '\\Str","canMove":"true"},' +
    '{"id":"a\" --launch \"b","name":"Quote","installDir":"' + $jroot + '\\Quote","canMove":true},' +
    '{"id":"--launch","name":"Dash","installDir":"' + $jroot + '\\Dash","canMove":true},' +
    '{"id":42,"name":"Number","installDir":"' + $jroot + '\\Number","canMove":true},' +
    '{"id":"long","name":"Long","installDir":["' + $jroot + '\\Long"],"canMove":true},' +
    '{"id":"unc","name":"Unc","installDir":"\\\\host\\share\\Game","canMove":true},' +
    '{"id":"dev","name":"Dev","installDir":"\\\\?\\' + $jroot + '\\Dev","canMove":true},' +
    '{"id":"rel","name":"Rel","installDir":"Games\\Rel","canMove":true},' +
    '{"id":"named","name":"' + $long + '","installDir":"' + $jroot + '\\Named","canMove":true},' +
    '"just a string", 7, null]}'
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", $hostile, (New-Object Text.UTF8Encoding($true)))   # with a BOM
$mover = & $move "$root\Mover"
$refused = @('Store', 'Str', 'Quote', 'Dash', 'Number', 'Long') | Where-Object { & $move "$root\$_" }
$odd = @('\\host\share\Game', "\\?\$root\Dev", 'Games\Rel') | Where-Object { & $gameAt $_ }
$named = & $gameAt "$root\Named"
"Move: $(Split-Path $mover[0] -Leaf) $($mover[1]); handed over for $(@($refused).Count) of 6 bad entries, $(@($odd).Count) of 3 UNC/device/relative folders named; a 5000-char name kept as $($named.Name.Length)"
if ($mover[1] -ne "--move `"$uuid`"" -or $mover[0] -ne "$PSScriptRoot\SpaceScan.exe" -or $refused -or $odd -or $named.Name.Length -ne 200) { throw 'MOVE MISMATCH' } else { 'MOVE OK' }
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", ($hostile -replace '"schema":1', '"schema":2'), $utf8)
$schema2 = & $move "$root\Mover"
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", $hostile + (' ' * 16MB), $utf8)
$huge = & $move "$root\Mover"
[IO.File]::WriteAllText("$ecoDir\data\shelf.games.json", $hostile, $utf8)
$shelfCard = [IO.File]::ReadAllText("$ecoDir\apps\shelf.json")
$cardMove = { param($exe) [IO.File]::WriteAllText("$ecoDir\apps\shelf.json", '{"schema":1,"paid":true,"edition":"full","exe":' + (ConvertTo-Json $exe) + '}', $utf8); & $move "$root\Mover" }
[IO.File]::WriteAllText("$root\Shelf.bat", '@echo off', $utf8)
$uncExe = & $cardMove '\\host\share\Shelf.exe'; $devExe = & $cardMove "\\?\$PSScriptRoot\SpaceScan.exe"
$batExe = & $cardMove "$root\Shelf.bat"; $relExe = & $cardMove 'SpaceScan.exe'; $goneExe = & $cardMove "$root\gone\Shelf.exe"
[IO.File]::WriteAllText("$ecoDir\apps\shelf.json", $shelfCard, $utf8)
$back = & $move "$root\Mover"
"Move refused: schema 2=$($null -eq $schema2), 16 MB file=$($null -eq $huge); Shelf's card exe on \\host=$($null -eq $uncExe), \\?\=$($null -eq $devExe), .bat=$($null -eq $batExe), relative=$($null -eq $relExe), gone=$($null -eq $goneExe); good card again=$($null -ne $back)"
if ($schema2 -or $huge -or $uncExe -or $devExe -or $batExe -or $relExe -or $goneExe -or -not $back) { throw 'MOVE INPUT MISMATCH' } else { 'MOVE INPUT OK' }

# Backup Labs: its backup folders and the folders it backs up are named, only while it is installed. The file is
# shaped as Backup Labs writes it (Ecosystem.JobsJson there: camelCase, UTC times, lastResult ok/failed/cancelled).
$node = { param($path) $n = [Activator]::CreateInstance($asm.GetType('Node')); $n.Name = $path
    $eco.GetMethod('Forget', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $null) | Out-Null; $n }
$into = { param($path) $eco.GetMethod('BackupsAt', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@(& $node $path)) }
$from = { param($path) $eco.GetMethod('BackedUpAt', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@(& $node $path)) }
$last = { param($job) $eco.GetMethod('LastBackup', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@($job)) }
$now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$jobs = '{"schema":1,"updated":"' + $now + '","jobs":[' +
    '{"id":"j1","name":"Photos","sources":["' + $jroot + '\\Photos"],"destination":"' + $jroot + '\\Backups\\","format":"folders","lastRun":"' + $now + '","lastResult":"ok"},' +
    '{"id":"j2","name":"Work","sources":["' + $jroot + '\\Work"],"destination":"E:\\Work backups","format":"encrypted","lastRun":"2026-01-01T10:00:00Z","lastResult":"failed"},' +
    '{"id":"j3","name":"New","sources":["' + $jroot + '\\New"],"destination":"","format":"zip","lastRun":null,"lastResult":null}]}'
[IO.File]::WriteAllText("$ecoDir\data\backuplabs.jobs.json", $jobs, $utf8)
$noBackupLabs = & $into "$root\Backups"
[IO.File]::WriteAllText("$ecoDir\apps\backuplabs.json", '{"schema":1,"paid":false,"edition":"free","exe":' + $self + '}', $utf8)
$dest = & $into "$root\backups"; $photos = & $from "$root\Photos"; $work = & $from "$root\Work"; $new = & $from "$root\New"; $plain = & $from "$root\Other"
"Backup Labs: without it=$noBackupLabs; backups folder of '$($dest.Name)'; Photos '$(& $last $photos)'; Work '$(& $last $work)'; New '$(& $last $new)'; other folder=$plain"
if ($noBackupLabs -or $dest.Id -ne 'j1' -or (& $last $photos) -ne 'backed up today' -or (& $last $work) -ne 'last backup failed' -or (& $last $new) -ne 'not backed up yet' -or $plain) { throw 'BACKUP LABS MISMATCH' } else { 'BACKUP LABS OK' }

# Updates: a failed check (offline, 403 rate limit, 5xx, an unreadable answer) throws; a readable "nothing newer" is null.
$upd = $asm.GetType('Updater')
$feed = $upd.GetField('Feed', [Reflection.BindingFlags]'Static,NonPublic')
$newer = $upd.GetMethod('Newer', [Reflection.BindingFlags]'Static,NonPublic')
$isFailure = $upd.GetMethod('Failed', [Reflection.BindingFlags]'Static,NonPublic')
function Serve($status, $body) {   # one request on localhost, answered from a second runspace
    $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $http = New-Object Net.HttpListener; $http.Prefixes.Add("http://localhost:$port/"); $http.Start()
    $ps = [powershell]::Create().AddScript({ param($http, $status, $body)
        $c = $http.GetContext(); $c.Response.StatusCode = $status; $b = [Text.Encoding]::UTF8.GetBytes($body)
        $c.Response.ContentLength64 = $b.Length; $c.Response.OutputStream.Write($b, 0, $b.Length); $c.Response.Close() }).AddArgument($http).AddArgument($status).AddArgument($body)
    $null = $ps.BeginInvoke()
    "http://localhost:$port/"
}
function Fails($url) { $feed.SetValue($null, $url); try { $newer.Invoke($null, $null) | Out-Null; $false } catch {
    $e = $_.Exception   # unwrap the reflection layers only: a WebException's own inner exception is the socket's
    while ($e -is [Management.Automation.MethodInvocationException] -or $e -is [Reflection.TargetInvocationException]) { $e = $e.InnerException }
    $isFailure.Invoke($null, [object[]]@($e)) } }
$offline = Fails 'http://127.0.0.1:9/'
$limited = Fails (Serve 403 '{"message":"API rate limit exceeded"}')
$busy = Fails (Serve 502 '')
$garbage = Fails (Serve 200 '<html>')
$feed.SetValue($null, (Serve 200 '{"tag_name":"v0.0.1","assets":[{"name":"SpaceScan-0.0.1-setup.exe","browser_download_url":"x","size":1,"digest":"sha256:00"}]}'))
$nothing = $newer.Invoke($null, $null)
"Update check fails: offline=$offline 403=$limited 502=$busy garbage=$garbage; nothing newer=$($nothing -eq $null)"
if (-not ($offline -and $limited -and $busy -and $garbage) -or $nothing -ne $null) { throw 'UPDATE CHECK MISMATCH' } else { 'UPDATE CHECK OK' }

# 1.4.0: the data folder moves into %APPDATA%\ProtagonistLabs\SpaceScan once, by a rename, never into a folder that
# holds data; a file held open leaves it for the next start. Synthetic folders only (MoveData takes both paths).
$moveData = { param($old, $new) $eco.GetMethod('MoveData', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@($old, $new)) }
$old = "$root\roaming\SpaceScan"; $new = "$root\roaming\ProtagonistLabs\SpaceScan"
function Put($dir, $n) { [IO.Directory]::CreateDirectory("$dir\history") | Out-Null; [IO.File]::WriteAllBytes("$dir\history\a.scan", (New-Object byte[] $n)) }
Put $old 10
$moved = (& $moveData $old $new) -eq $new -and -not (Test-Path $old) -and (Get-Item "$new\history\a.scan").Length -eq 10
$again = (& $moveData $old $new) -eq $new -and (Get-Item "$new\history\a.scan").Length -eq 10
Put $old 20
$kept = (& $moveData $old $new) -eq $new -and (Get-Item "$old\history\a.scan").Length -eq 20 -and (Get-Item "$new\history\a.scan").Length -eq 10 -and (Get-Content "$new\move.log" -Raw) -match 'left where it is'
Remove-Item $new -Recurse -Force; [IO.Directory]::CreateDirectory($new) | Out-Null
$empty = (& $moveData $old $new) -eq $new -and -not (Test-Path $old) -and (Get-Item "$new\history\a.scan").Length -eq 20
Remove-Item $new -Recurse -Force; Put $old 30
$held = [IO.File]::Open("$old\history\a.scan", 'Open', 'Read', 'None')
$locked = (& $moveData $old $new) -eq $old -and (Test-Path "$old\history\a.scan") -and -not (Test-Path "$new\history")
$held.Close()
$retry = (& $moveData $old $new) -eq $new -and -not (Test-Path $old)
"Data move: moved=$moved again=$again both-hold-data-kept-apart=$kept empty-new-taken=$empty locked-uses-old=$locked next-start=$retry"
if (-not ($moved -and $again -and $kept -and $empty -and $locked -and $retry)) { throw 'DATA MOVE MISMATCH' } else { 'DATA MOVE OK' }

# Sisters by uninstall entry (File Labs): nothing for a key no installer wrote. What's new: built in, every release
# readable, the newest one the version being built, each with its picture or its month's.
$noApp = $eco.GetMethod('Uninstalled', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@('{00000000-0000-0000-0000-000000000000}_is1'))
$notes = $asm.GetType('WhatsNew').GetMethod('Load', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $null)
$picture = $asm.GetType('WhatsNew').GetMethod('Picture', [Reflection.BindingFlags]'Static,NonPublic')
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo("$PSScriptRoot\SpaceScan.exe").FileVersion
"What's new: $($notes.Count) releases, newest $($notes[0].Version) ($($notes[0].Lines.Count) lines), exe $version; unknown uninstall key=$($null -eq $noApp)"
if ($noApp -or $notes.Count -lt 9 -or $notes[0].Version -ne $version -or ($notes | Where-Object { $_.Lines.Count -eq 0 -or -not $_.Title -or -not $picture.Invoke($null, [object[]]@($_.psobject.BaseObject)) })) { throw 'WHATS NEW MISMATCH' } else { 'WHATS NEW OK' }

cmd /c rd /s /q "\\?\$root"

# MFT path (admin only): C:\Windows totals from the MFT must match the classic walk within 2%.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) { 'MFT check skipped (run elevated)'; return }
$mftRoot = $asm.GetType('Mft').GetMethod('Scan').Invoke($null, [object[]]@('C:\'))
$win = $mftRoot.Dirs | Where-Object { $_.Name -eq 'Windows' }
$classic = [Activator]::CreateInstance($asm.GetType('Node')); $classic.Name = 'C:\Windows'
$asm.GetType('Scanner').GetMethod('Scan').Invoke($null, [object[]]@($classic, 'C:\Windows')) | Out-Null
"MFT:     $($win.Files) files, $($win.Size) bytes (whole C: $($mftRoot.Files) files)"
"Classic: $($classic.Files) files, $($classic.Size) bytes"
if ([math]::Abs($win.Files - $classic.Files) / $classic.Files -gt 0.02 -or [math]::Abs($win.Size - $classic.Size) / $classic.Size -gt 0.02) { throw 'MFT MISMATCH' } else { 'MFT OK' }

# MFT + a path rule naming one file: that file's bytes must disappear from its folder's total.
$probe = 'C:\Windows\explorer.exe'
$probeSize = (Get-Item $probe).Length
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@($probe)))
$mft2 = $asm.GetType('Mft').GetMethod('Scan').Invoke($null, [object[]]@('C:\'))
$exclude.GetMethod('Set').Invoke($null, [object[]]@(, [string[]]@()))
$win2 = $mft2.Dirs | Where-Object { $_.Name -eq 'Windows' }
"MFT file path rule: Windows shrank by $($win.Size - $win2.Size) bytes, explorer.exe is $probeSize"
if ($win.Size - $win2.Size -ne $probeSize -or $win.Files - $win2.Files -ne 1) { throw 'MFT EXCLUDE MISMATCH' } else { 'MFT EXCLUDE OK' }
