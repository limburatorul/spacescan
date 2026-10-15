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
