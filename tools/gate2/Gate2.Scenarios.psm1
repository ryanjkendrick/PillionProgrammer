#Requires -Version 7.5
<#
.SYNOPSIS
    Helpers for tools/gate2/Invoke-Gate2.ps1 (work item 2.17): the generated solution the IDE scenarios open (a git repository
    with a realistic-diff branch and a whole-file-rewrite branch touching 500 files, and two secret-bearing files), the
    generated-repository indexing runs of step d, the scripted checks of encryption at rest, the two-instance check, the
    index clean-up, and the Gate 2 criteria table. Uses tools/gate0/Gate0.Checks.psm1 and tools/gate1/Gate1.Scenarios.psm1
    (imported by the caller) for logging, settings, installation, test runs and TRX reading.
#>

Set-StrictMode -Version 3.0

#region The generated solution

function New-Gate2CodeFile {
    <#
    One generated C# file of about $Lines lines: a namespace, one class with methods that each declare a few locals, loop and
    call a neighbour. $Variant 'base' is the committed text; 'realistic' changes the bodies of two methods (as an edit or a
    small feature would); 'rewrite' renames everything (a whole-file rewrite). Deterministic.
    #>
    param([Parameter(Mandatory)][int]$Index, [int]$Lines = 170, [ValidateSet('base', 'realistic', 'rewrite')][string]$Variant = 'base')
    $sb = [System.Text.StringBuilder]::new($Lines * 48)
    $prefix = if ($Variant -eq 'rewrite') { 'Rewritten' } else { 'Service' }
    $module = $Index % 12
    [void]$sb.Append("using System;`nusing System.Collections.Generic;`nusing System.Linq;`n`nnamespace Generated.Module$module`n{`n")
    [void]$sb.Append("    /// <summary>Generated type $Index ($Variant).</summary>`n    public sealed class $prefix$Index`n    {`n")
    [void]$sb.Append("        private readonly List<int> _items = new List<int>();`n        private readonly Dictionary<string, int> _totals = new Dictionary<string, int>();`n`n")
    $written = 12
    $method = 0
    while ($written -lt $Lines - 3) {
        $name = if ($Variant -eq 'rewrite') { "Recompute${Index}_$method" } else { "Process${Index}_$method" }
        $next = if ($Variant -eq 'rewrite') { "Recompute${Index}_$($method + 1)" } else { "Process${Index}_$($method + 1)" }
        [void]$sb.Append("        /// <summary>Handles step $method of type $Index.</summary>`n")
        [void]$sb.Append("        public int $name(int seed, string label)`n        {`n")
        [void]$sb.Append("            var total = seed;`n            foreach (var item in _items)`n            {`n                total += item * $($method + 3);`n            }`n`n")
        if ($Variant -eq 'realistic' -and ($method -eq 1 -or $method -eq 4)) {
            [void]$sb.Append("            // Changed on the feature branch: labels are counted too.`n            total += label.Length * $($method + 7); // BranchMarker${Index}_$method`n")
            $written += 2
        }
        if ($Variant -eq 'rewrite') {
            [void]$sb.Append("            total ^= label.GetHashCode(); // RewriteMarker${Index}_$method`n")
            $written += 1
        }
        [void]$sb.Append("            _totals[label] = total;`n            return total > $(100 + $method) ? $next(total - 1, label) : total;`n        }`n`n")
        $written += 16
        $method++
    }
    [void]$sb.Append("        public int $($(if ($Variant -eq 'rewrite') { 'Recompute' } else { 'Process' }))${Index}_$method(int seed, string label) => seed + label.Length;`n    }`n}`n")
    return $sb.ToString()
}

function Invoke-Git {
    <# Runs git in a folder; throws on a non-zero exit. Returns the output lines. #>
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string[]]$Arguments)
    $output = & git -C $Folder @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in ${Folder}: $($output -join ' ')" }
    return $output
}

function New-Gate2Solution {
    <#
    The solution the IDE scenarios open: $Files generated C# files of about $LinesPerFile lines (about 100,000 lines), a
    classic C# project that compiles them all, a .gitignore, two secret-bearing files (.env and appsettings.Development.json,
    fake values) and empty files the typing passes use. A git repository: master holds the base text; branch 'realistic'
    changes two methods in each of the first 500 files; branch 'rewrite' rewrites the same 500 files whole. Returns the folder.
    #>
    param([Parameter(Mandatory)][string]$Root, [int]$Files = 600, [int]$LinesPerFile = 200, [int]$BranchFiles = 500)
    $folder = Join-Path $Root 'gate2-solution'
    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $folder 'src') | Out-Null
    $lines = 0
    for ($i = 0; $i -lt $Files; $i++) {
        $dir = Join-Path $folder "src\Module$($i % 12)"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $text = New-Gate2CodeFile -Index $i -Lines $LinesPerFile
        [IO.File]::WriteAllText((Join-Path $dir "File$i.cs"), $text)
        $lines += ($text.Split("`n").Count - 1)
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $folder 'typing') | Out-Null
    foreach ($f in 'TypeIndexing.cs', 'TypeIdle.cs', 'SaveNew.cs') { [IO.File]::WriteAllText((Join-Path $folder "typing\$f"), '') }
    [IO.File]::WriteAllText((Join-Path $folder 'Program.cs'), "namespace Generated`n{`n    internal static class Program`n    {`n        private static void Main()`n        {`n        }`n    }`n}`n")
    # Fake secrets: the discovery rules exclude these files by name and the secret scanner would flag their content.
    [IO.File]::WriteAllText((Join-Path $folder '.env'), "API_KEY=sk-gate2-fake-0000000000000000000000000000`nDB_PASSWORD=not-a-real-password`n")
    [IO.File]::WriteAllText((Join-Path $folder 'appsettings.Development.json'), "{`n  `"ConnectionStrings`": { `"Main`": `"Server=.;Database=Gate2;User Id=sa;Password=FakeGate2Password1!`" }`n}`n")
    [IO.File]::WriteAllText((Join-Path $folder '.gitignore'), "bin/`nobj/`n.vs/`n")
    $guid = '{6A2B3C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D}'
    $csproj = @"
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="`$(MSBuildExtensionsPath)\`$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('`$(MSBuildExtensionsPath)\`$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <PropertyGroup>
    <Configuration Condition=" '`$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '`$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>$guid</ProjectGuid>
    <OutputType>Exe</OutputType>
    <RootNamespace>Generated</RootNamespace>
    <AssemblyName>Gate2Generated</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
  <PropertyGroup Condition=" '`$(Configuration)|`$(Platform)' == 'Debug|AnyCPU' ">
    <OutputPath>bin\Debug\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Core" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="src\**\*.cs" />
    <Compile Include="typing\*.cs" />
  </ItemGroup>
  <Import Project="`$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $folder 'Gate2.csproj'), $csproj)
    $sln = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Gate2", "Gate2.csproj", "$guid"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		$guid.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		$guid.Debug|Any CPU.Build.0 = Debug|Any CPU
	EndGlobalSection
EndGlobal
"@
    [IO.File]::WriteAllText((Join-Path $folder 'Gate2.sln'), $sln)

    [void](Invoke-Git $folder @('init', '-q', '-b', 'master'))
    [void](Invoke-Git $folder @('config', 'user.email', 'gate2@example.invalid'))
    [void](Invoke-Git $folder @('config', 'user.name', 'Gate 2'))
    [void](Invoke-Git $folder @('config', 'core.autocrlf', 'false'))
    # The secret-bearing files are committed on purpose: the index must leave them out whatever git thinks.
    [void](Invoke-Git $folder @('add', '-A'))
    [void](Invoke-Git $folder @('commit', '-q', '-m', 'base'))
    foreach ($variant in 'realistic', 'rewrite') {
        [void](Invoke-Git $folder @('checkout', '-q', '-b', $variant, 'master'))
        for ($i = 0; $i -lt $BranchFiles; $i++) {
            [IO.File]::WriteAllText((Join-Path $folder "src\Module$($i % 12)\File$i.cs"), (New-Gate2CodeFile -Index $i -Lines $LinesPerFile -Variant $variant))
        }
        [void](Invoke-Git $folder @('commit', '-q', '-a', '-m', $variant))
        [void](Invoke-Git $folder @('checkout', '-q', 'master'))
    }
    $stats = Invoke-Git $folder @('diff', '--shortstat', 'master', 'rewrite')
    [pscustomobject]@{ Folder = $folder; Solution = Join-Path $folder 'Gate2.sln'; Files = $Files; Lines = $lines; BranchFiles = $BranchFiles; RewriteDiff = ($stats -join ' ').Trim(); RealisticDiff = ((Invoke-Git $folder @('diff', '--shortstat', 'master', 'realistic')) -join ' ').Trim() }
}

#endregion

#region Indexes in the data folder

function Get-PillionIndexFolders {
    <# The index folders under %LOCALAPPDATA%\PillionProgrammer\index with the root their state.json names. #>
    param([string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PillionProgrammer'))
    $indexRoot = Join-Path $DataRoot 'index'
    if (-not (Test-Path $indexRoot)) { return @() }
    foreach ($d in Get-ChildItem -LiteralPath $indexRoot -Directory) {
        $root = $null
        $state = Join-Path $d.FullName 'state.json'
        if (Test-Path $state) { try { $root = (Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).rootPath } catch { } }
        $bytes = (Get-ChildItem -LiteralPath $d.FullName -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        [pscustomobject]@{ Folder = $d.FullName; Id = $d.Name; Root = $root; Bytes = $bytes; Files = @(Get-ChildItem -LiteralPath $d.FullName -File -ErrorAction SilentlyContinue | ForEach-Object Name) }
    }
}

function Remove-Gate2Indexes {
    <# Deletes the index folders whose root is under $UnderPath (the gate's scratch folder); returns what was removed. #>
    param([Parameter(Mandatory)][string]$UnderPath, [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PillionProgrammer'))
    $removed = @()
    foreach ($i in @(Get-PillionIndexFolders -DataRoot $DataRoot)) {
        if ($i.Root -and $i.Root.StartsWith($UnderPath, [StringComparison]::OrdinalIgnoreCase)) {
            try { Remove-Item -LiteralPath $i.Folder -Recurse -Force; $removed += "$($i.Id) ($($i.Root))" }
            catch { $removed += "$($i.Id) ($($i.Root)): NOT removed: $($_.Exception.Message)" }
        }
    }
    return $removed
}

#endregion

#region Encryption at rest

function Test-PlainSqliteOpen {
    <#
    Tries to read a database file with plain SQLite (Python's sqlite3, no cipher): an encrypted file fails with "file is
    not a database". Returns the outcome text. Uses a copy, so the live file is never touched.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Scratch)
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $python) { return [pscustomobject]@{ Path = $Path; Opened = $null; Outcome = 'not checked: no python on PATH' } }
    $copy = Join-Path $Scratch ("plain-" + [IO.Path]::GetFileName($Path))
    Copy-Item -LiteralPath $Path -Destination $copy -Force
    $script = "import sqlite3,sys`ntry:`n    c=sqlite3.connect(sys.argv[1]); r=c.execute('select count(*) from sqlite_master').fetchone(); print('OPENED', r[0])`nexcept Exception as e:`n    print('FAILED', type(e).__name__, e)"
    $py = Join-Path $Scratch 'plain-open.py'
    Set-Content -LiteralPath $py -Value $script -Encoding utf8
    $out = (& $python.Source -I $py $copy 2>&1) -join ' '
    Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue
    [pscustomobject]@{ Path = $Path; Opened = ($out -like 'OPENED*'); Outcome = $out }
}

function Get-KeyFileAcl {
    <# Who may open a DPAPI key file: the access rules and whether inheritance is off. #>
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.Access | ForEach-Object { "$($_.IdentityReference) $($_.AccessControlType) $($_.FileSystemRights)$(if ($_.IsInherited) { ' (inherited)' })" })
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $others = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $me, 'NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators' })
    [pscustomobject]@{ Path = $Path; InheritanceProtected = $acl.AreAccessRulesProtected; Rules = $rules; OthersAllowed = @($others | ForEach-Object { "$($_.IdentityReference)" }); UserOnly = ($others.Count -eq 0) }
}

function Find-PlainText {
    <#
    Searches every file under the folders for a marker string (UTF-8 and UTF-16), skipping the source tree itself: a hit
    outside the database means chunk text was written in plain text somewhere. Returns the files that hold it.
    #>
    param([Parameter(Mandatory)][string[]]$Folders, [Parameter(Mandatory)][string]$Marker, [string[]]$Skip = @())
    $utf8 = [Text.Encoding]::UTF8.GetBytes($Marker)
    $utf16 = [Text.Encoding]::Unicode.GetBytes($Marker)
    $hits = [System.Collections.Generic.List[string]]::new()
    $searched = 0
    foreach ($folder in $Folders) {
        if (-not (Test-Path $folder)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $folder -Recurse -File -Force -ErrorAction SilentlyContinue) {
            if (@($Skip | Where-Object { $f.FullName.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
            if ($f.Length -gt 1GB) { continue }
            $searched++
            try {
                $bytes = [IO.File]::ReadAllBytes($f.FullName)
                if ((Find-Bytes $bytes $utf8) -or (Find-Bytes $bytes $utf16)) { $hits.Add($f.FullName) }
            }
            catch { } # A file another process holds exclusively (a live WAL); the database files are checked by the plain-open test.
        }
    }
    [pscustomobject]@{ Marker = $Marker; FilesSearched = $searched; Hits = $hits.ToArray() }
}

function Find-Bytes([byte[]]$Haystack, [byte[]]$Needle) {
    if ($Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) { return $false }
    $first = $Needle[0]
    $i = [Array]::IndexOf($Haystack, $first)
    while ($i -ge 0 -and $i -le $Haystack.Length - $Needle.Length) {
        $match = $true
        for ($j = 1; $j -lt $Needle.Length; $j++) { if ($Haystack[$i + $j] -ne $Needle[$j]) { $match = $false; break } }
        if ($match) { return $true }
        $i = [Array]::IndexOf($Haystack, $first, $i + 1)
    }
    return $false
}

#endregion

#region Two instances, one writer, stale-lock reclaim

function Read-WriterLock {
    <# The writer.lock of an index folder (owner pid, start time, pipe), or $null. #>
    param([Parameter(Mandatory)][string]$IndexFolder)
    $path = Join-Path $IndexFolder 'writer.lock'
    if (-not (Test-Path $path)) { return $null }
    try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $null }
}

function Test-TwoInstancesShareOneIndex {
    <#
    Opens the same folder in two Visual Studio instances (/rootsuffix $RootSuffix, Pillion installed there), waits until both
    have a Pillion Host and the index folder for that root has a writer.lock, checks that the lock names one of the two
    hosts, then kills the owner's Visual Studio and its host (a hard kill leaves a stale lock) and waits for the other host
    to take the index over (its pid in writer.lock). Closes what it launched. Returns the timeline.
    #>
    param([Parameter(Mandatory)]$VisualStudio, [Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$RootSuffix, [int]$TakeoverTimeoutSec = 180)
    $launched = @()
    $timeline = [System.Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $note = { param($t) $timeline.Add(('{0,6:0.0} s  {1}' -f $clock.Elapsed.TotalSeconds, $t)); Write-Gate0Log "two-instances: $t" }
    try {
        foreach ($n in 1, 2) {
            $p = Start-Process -FilePath $VisualStudio.DevEnv -ArgumentList @('/rootsuffix', $RootSuffix, "`"$Folder`"") -PassThru
            $launched += $p
            & $note "launched devenv $n (pid $($p.Id))"
            Start-Sleep -Seconds 20
        }
        $deadline = (Get-Date).AddSeconds(240)
        $hosts = @{}
        $index = $null
        $lock = $null
        while ((Get-Date) -lt $deadline) {
            foreach ($p in $launched) {
                $h = @(Get-CimInstance Win32_Process -Filter "Name='Pillion.Host.exe' AND ParentProcessId=$($p.Id)")
                if ($h.Count) { $hosts[$p.Id] = [int]$h[0].ProcessId }
            }
            $index = @(Get-PillionIndexFolders | Where-Object { $_.Root -and [IO.Path]::GetFullPath($_.Root).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Folder).TrimEnd('\') }) | Select-Object -First 1
            if ($index) { $lock = Read-WriterLock -IndexFolder $index.Folder }
            if ($hosts.Count -eq 2 -and $lock) { break }
            Start-Sleep -Seconds 2
        }
        if ($hosts.Count -ne 2) { throw "Expected a Pillion Host in each Visual Studio; found $($hosts.Count)." }
        if (-not $lock) { throw "No writer.lock for $Folder after 240 s (index folder: $(if ($index) { $index.Folder } else { 'none' }))." }
        & $note "hosts: $(($hosts.GetEnumerator() | ForEach-Object { "devenv $($_.Key) -> host $($_.Value)" }) -join '; '); writer.lock pid $($lock.processId) in $($index.Folder)"
        $ownerDevenv = @($hosts.GetEnumerator() | Where-Object { $_.Value -eq [int]$lock.processId } | ForEach-Object { $_.Key })
        if ($ownerDevenv.Count -ne 1) { throw "writer.lock names pid $($lock.processId), which is neither host ($($hosts.Values -join ', '))." }
        $ownerHost = [int]$lock.processId
        $otherHost = @($hosts.Values | Where-Object { $_ -ne $ownerHost })[0]
        # Hard kill: the owner's Visual Studio first (its host would otherwise be restarted), then the host itself.
        Stop-Process -Id $ownerDevenv[0] -Force -ErrorAction SilentlyContinue
        Stop-Process -Id $ownerHost -Force -ErrorAction SilentlyContinue
        $killedAt = $clock.Elapsed
        & $note "killed devenv $($ownerDevenv[0]) and its host $ownerHost; writer.lock still on disk: $([bool](Read-WriterLock -IndexFolder $index.Folder))"
        $takenOver = $null
        $deadline = (Get-Date).AddSeconds($TakeoverTimeoutSec)
        while ((Get-Date) -lt $deadline) {
            $l = Read-WriterLock -IndexFolder $index.Folder
            if ($l -and [int]$l.processId -eq $otherHost) { $takenOver = $clock.Elapsed - $killedAt; break }
            Start-Sleep -Milliseconds 500
        }
        if ($takenOver) { & $note ("host $otherHost took the index over after {0:0.0} s (writer.lock now names it)" -f $takenOver.TotalSeconds) }
        else { & $note "host $otherHost did NOT take the index over within $TakeoverTimeoutSec s" }
        [pscustomobject]@{ Folder = $Folder; IndexFolder = $index.Folder; Hosts = @($hosts.Values); Writer = $ownerHost; OneWriter = $true; TakeoverSeconds = $(if ($takenOver) { [math]::Round($takenOver.TotalSeconds, 1) } else { $null }); Timeline = $timeline.ToArray() }
    }
    finally {
        foreach ($p in $launched) {
            try { $p.Refresh(); if (-not $p.HasExited) { [void](Stop-LaunchedDevenv -Process $p) } } catch { }
        }
    }
}

#endregion

#region Figures from the tests

function Get-Gate2BenchmarkFigures {
    <# The figures the Benchmark and Model tests print (lines kept verbatim), by test. #>
    param([object[]]$Results = @())
    $pick = { param($test, $pattern) @(Select-OutputLines -Results $Results -TestPattern $test -LinePattern $pattern | ForEach-Object { "$($_.Outcome): $($_.Line)" }) }
    [ordered]@{
        indexingThroughput = & $pick 'IndexingModelTests\.Throughput' 'lines/min'
        indexingTokens     = & $pick 'TheFixtureRepositories_AverageTokensPerChunk' 'tokens per chunk'
        queryEmbedding     = & $pick 'IndexingCatalogTests|QueryEmbedding' 'p50|p95'
        saveSearchable     = & $pick 'IndexingWatcherModelTests' 'ms|searchable'
        branchSwitch       = & $pick 'IndexingWatcherModelTests' 'checkout|idle|branch'
        retrieval          = & $pick 'RetrievalBenchmarkTests|RetrievalTests' 'p50|p95'
        storage            = & $pick 'IndexPerformanceTests' 'p50|p95|bytes|chunk'
        contextAssembly    = & $pick 'ContextAssemblyBenchmarkTests' 'p50|p95|assembly'
        bundledModel       = & $pick 'BundledEmbeddingModelTests' 'chunks/s|p50|ms'
        evalSmoke          = & $pick 'RetrievalSmokeTests' 'recall|MRR|@10'
    }
}

#endregion

#region The Gate 2 table

function Get-G2 {
    <# Walks a dotted path through dictionaries and objects; $null when any part is missing. #>
    param($Object, [Parameter(Mandatory)][string]$Path)
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        if ($current -is [System.Collections.IDictionary]) { $current = if ($current.Contains($part)) { $current[$part] } else { $null } }
        elseif ($current.PSObject.Properties.Name -contains $part) { $current = $current.$part }
        else { return $null }
    }
    return $current
}

function Get-G2Count { param($Value) if ($null -eq $Value) { 0 } else { @($Value).Count } }

function New-Gate2Table {
    <#
    The Gate 2 criteria table of this run (one Visual Studio version): per row the target, what was measured, a status (Pass,
    Fail, Pending product owner, Not measured) and how. Returns the rows and Markdown.
    #>
    param([System.Collections.IDictionary]$Data, [object[]]$Steps, $VisualStudio)
    $v = if ($VisualStudio) { "VS$($VisualStudio.Major)" } else { 'VS?' }
    $sc = { param($name) $s = Get-G2 $Data 'ide.scenarios'; if ($s -and $s.Contains("$name-$v")) { $s["$name-$v"] } else { $null } }
    $tests = @(Get-G2 $Data 'ide.results')
    $outcome = { param($pattern) $t = @($tests | Where-Object { $_ -and $_.name -match $pattern }); if ($t.Count -eq 0) { 'not run' } else { ($t | ForEach-Object { $_.outcome }) -join '/' } }
    $step = { param($prefix) $Steps | Where-Object { $_.name -like "$prefix*" } | Select-Object -First 1 }
    $rows = [System.Collections.Generic.List[object]]::new()
    $add = { param($criterion, $target, $measured, $status, $how) $rows.Add([ordered]@{ criterion = $criterion; target = $target; measured = $measured; status = $status; how = $how }) }
    $ms = { param($x) if ($null -eq $x) { 'n/a' } else { '{0:0} ms' -f [double]$x } }
    $runs = @($Data['indexRuns'])
    $find = { param($source, $lines, $ep) $runs | Where-Object { $_ -and $_.source -eq $source -and [int]$_.requestedLines -eq $lines -and $_.executionProviderRequested -eq $ep } | Select-Object -First 1 }
    $describeRun = { param($r) if (-not $r) { 'not run' } else { '{0}: {1:N0} lines, {2:N0} chunks in {3:0.0} min ({4:N0} lines/min, {5:0.0} chunks/s, {6:0.0} tokens/chunk), first searchable after {7} s, {8:N0} chunks searchable at 1 min, {9:N0} B/chunk ({10:N0} with WAL), peak working set {11:N0} MB, {12} memory pauses, EP {13}' -f $r.label, $r.lines, $r.chunks, $r.minutes, $r.linesPerMinute, $r.chunksPerSecond, $r.tokensPerChunk, $r.firstSearchableSeconds, $r.chunksAtOneMinute, $r.bytesPerChunk, $r.bytesPerChunkWithWal, $r.peakWorkingSetMB, $r.memoryPauses, $r.executionProviderLoaded } }
    $bigLines = @($runs | ForEach-Object { [int]$_.requestedLines }) | Sort-Object -Descending | Select-Object -First 1
    $smallLines = @($runs | ForEach-Object { [int]$_.requestedLines }) | Sort-Object | Select-Object -First 1

    # Retrieval.
    $ret = $Data['retrieval']
    $bge = @(Get-G2 $ret 'hybrid') | Where-Object { $_ -and $_.model -like '*bge-small*' } | Select-Object -First 1
    & $add 'Retrieval quality' 'Recall@10 0.85 and MRR, bundled model; 0.90 with the code model if it ships; int8 loss within 2 points' $(if ($ret) { "hybrid recall@10 by file / MRR on pillion-src (60 queries): $((@($ret.hybrid) | ForEach-Object { "$($_.model) $($_.recall10) / $($_.mrr)" }) -join '; '); int8 loss: $($ret.int8Loss -join '; ')" } else { 'no baseline document' }) $(if ($bge -and $bge.recall10 -ge 0.85) { 'Pass (this repository''s sets; the external fixture repositories wait on issue #50)' } elseif ($bge) { 'Fail' } else { 'Not measured' }) 'docs/eval-retrieval-baseline.md (work item 2.16), quoted'

    # Initial index.
    foreach ($row in @(@{ Lines = $smallLines; Name = 'Initial index, 100,000 lines, bundled model, CPU'; Target = '3 minutes or less'; Limit = 3 }, @{ Lines = $bigLines; Name = 'Initial index, 1,000,000 lines, CPU'; Target = '30 minutes or less, in the background, partial results searchable after the first minute'; Limit = 30 })) {
        if (-not $row.Lines) { & $add $row.Name $row.Target 'not run' 'Not measured' ''; continue }
        $syn = & $find 'synthetic' $row.Lines 'cpu'; $real = & $find 'real' $row.Lines 'cpu'
        $dml = @(@(& $find 'synthetic' $row.Lines 'directml'; & $find 'real' $row.Lines 'directml') | Where-Object { $_ })
        $measured = "CPU: $(& $describeRun $syn); $(& $describeRun $real)" + $(if ($dml.Count) { "; DirectML (issue #78, reported separately): " + (($dml | ForEach-Object { & $describeRun $_ }) -join '; ') } else { '' })
        $judged = if ($real) { $real } else { $syn }
        $partialOk = $row.Limit -lt 30 -or ($judged -and $null -ne $judged.firstSearchableSeconds -and $judged.firstSearchableSeconds -le 60)
        $status = if (-not $judged) { 'Not measured' } elseif ($judged.minutes -le $row.Limit -and $partialOk) { 'Pass' } elseif ($syn -and $syn.minutes -le $row.Limit) { 'Fail on real code (generated code passes); decision pending product owner (#90)' } else { 'Fail; decision pending product owner (#90)' }
        & $add $row.Name $row.Target $measured $status 'Gate2IndexingTests: the real IndexingService composed in-process, real gateway, bundled bge-small int8, the real working set governing the memory cap'
    }

    # Save to searchable.
    $save = & $sc 'gate2-save-searchable'
    $editP95 = Get-G2 $save 'editsSearchable.p95'; $editMax = Get-G2 $save 'editsSearchable.max'
    & $add 'One saved file becomes searchable' '500 ms or less' "one-member edits: p50 $(& $ms (Get-G2 $save 'editsSearchable.p50')), p95 $(& $ms $editP95), max $(& $ms $editMax) ($(Get-G2 $save 'editsSearchable.count') saves); whole-file rewrites: p50 $(& $ms (Get-G2 $save 'rewritesSearchable.p50')), max $(& $ms (Get-G2 $save 'rewritesSearchable.max')) (lexical in full; vectors partly, the rest right after)" $(if ($null -eq $editP95) { 'Not measured' } elseif ($editMax -le 500) { 'Pass' } elseif ($editP95 -le 500) { 'Pass at p95 (max over)' } else { 'Fail' }) 'IDE: DTE Document.Save, then lexical SearchDetailedAsync every 10 ms'

    # Branch switch.
    $bReal = & $sc 'gate2-branch-realistic'; $bRew = & $sc 'gate2-branch-rewrite'
    $rs = Get-G2 $bReal 'forward.searchableSeconds'; $ri = Get-G2 $bReal 'forward.idleSeconds'
    & $add 'Branch switch touching 500 files' '30 s or less' "realistic diff (2 members in each of 500 files): searchable after $rs s, idle after $ri s (git $(Get-G2 $bReal 'forward.gitSeconds') s); whole-file rewrite: searchable after $(Get-G2 $bRew 'forward.searchableSeconds') s, idle after $(Get-G2 $bRew 'forward.idleSeconds') s (git $(Get-G2 $bRew 'forward.gitSeconds') s)" $(if ($null -eq $ri) { 'Not measured' } elseif ($ri -le 30) { 'Pass (realistic diff; definition pending product owner)' } else { 'Fail' }) 'IDE: git checkout with Visual Studio open; the host''s HEAD watcher and hash sweep'

    # Search.
    $search = & $sc 'gate2-search'
    $sp50 = Get-G2 $search 'client.p50'; $sp95 = Get-G2 $search 'client.p95'
    & $add 'Semantic search query' 'p50 100 ms, p95 300 ms on a 100,000-chunk index, hybrid without the reranker' "$(Get-G2 $search 'root.chunks') chunks: from devenv.exe p50 $(& $ms $sp50), p95 $(& $ms $sp95); host p50 $(& $ms (Get-G2 $search 'host.p50')), p95 $(& $ms (Get-G2 $search 'host.p95')); degraded $(Get-G2 $search 'degraded')" $(if ($null -eq $sp50) { 'Not measured' } elseif ($sp50 -le 100 -and $sp95 -le 300) { 'Pass' } else { 'Fail' }) 'IDE: SearchDetailedAsync, 40 queries after 3 warm-ups'

    # Context assembly.
    $ctx = & $sc 'gate2-context-assembly'
    $cp50 = Get-G2 $ctx 'assembly.p50'; $cp95 = Get-G2 $ctx 'assembly.p95'
    & $add 'Context assembly with retrieval' 'p50 150 ms, p95 400 ms on a 100,000-chunk index' "p50 $(& $ms $cp50), p95 $(& $ms $cp95); $(Get-G2 $ctx 'retrievedItemsPerRequest') retrieved items per request; kinds $((@(Get-G2 $ctx 'itemKinds')) -join ', ')" $(if ($null -eq $cp50) { 'Not measured' } elseif ($cp50 -le 150 -and $cp95 -le 400) { 'Pass' } else { 'Fail' }) 'IDE: PreviewContextAsync (the Context Inspector), 40 requests after 3 warm-ups'

    # Workspace queries.
    $wq = & $sc 'gate2-workspace-queries'
    $wq50 = Get-G2 $wq 'symbolMentions.p50'; $wq95 = Get-G2 $wq 'symbolMentions.p95'
    $wqTest = & $outcome 'WorkspaceQueries_FindReferencesAndCallersOf'
    & $add 'Workspace query answered by devenv.exe' 'p50 300 ms, p95 2 s, off the UI thread, one in flight, at most 5 a second' "@symbol through the host on the generated solution: p50 $(& $ms $wq50), p95 $(& $ms $wq95); integration test (UI thread, one in flight, rate) $wqTest" $(if ($null -eq $wq50) { if ($wqTest -eq 'Passed') { 'Pass (integration test)' } else { 'Not measured' } } elseif ($wq50 -le 300 -and $wq95 -le 2000 -and $wqTest -eq 'Passed') { 'Pass' } else { 'Fail' }) 'IDE: ResolveMentionAsync(@symbol) x20; WorkspaceQueries integration test'

    # Memory and disk.
    $peaks = @($runs | Where-Object { $_ } | ForEach-Object { "$($_.label) $($_.peakWorkingSetMB) MB ($($_.memoryPauses) pauses, batch $($_.batchSizeAtEnd) at the end)" })
    $maxPeak = (@($runs | Where-Object { $_ } | ForEach-Object { [double]$_.peakWorkingSetMB }) | Measure-Object -Maximum).Maximum
    & $add 'Host memory while indexing' '1.5 GB or less by default; batch halves under pressure, then indexing pauses' "peak working set of the indexing process: $($peaks -join '; ')" $(if ($null -eq $maxPeak) { 'Not measured' } elseif ($maxPeak -le 1536) { 'Pass' } else { 'Fail' }) 'Gate2IndexingTests (working set sampled before each batch, the memory governor live); unit tests of the governor'
    $bpc = @($runs | Where-Object { $_ } | ForEach-Object { "$($_.label) $($_.bytesPerChunk) B ($($_.bytesPerChunkWithWal) with WAL)" })
    $realBpc = (@($runs | Where-Object { $_ -and $_.source -eq 'real' } | ForEach-Object { [double]$_.bytesPerChunk }) | Measure-Object -Maximum).Maximum
    $synBpc = (@($runs | Where-Object { $_ -and $_.source -eq 'synthetic' } | ForEach-Object { [double]$_.bytesPerChunk }) | Measure-Object -Maximum).Maximum
    & $add 'Index on disk' 'About 1.5 KB per chunk' ($bpc -join '; ') $(if ($null -eq $realBpc -and $null -eq $synBpc) { 'Not measured' } elseif (($realBpc ?? $synBpc) -le 1700) { 'Pass' } else { 'Fail on real code; decision pending product owner (#90)' }) 'database size over chunks after the run (and with the WAL)'

    # Indexing never disturbs the IDE.
    $kIdx = & $sc 'gate2-keystrokes-indexing'; $kIdle = & $sc 'gate2-keystrokes-idle'
    $a50 = Get-G2 $kIdx 'typing.typeChar.p50'; $a95 = Get-G2 $kIdx 'typing.typeChar.p95'; $b50 = Get-G2 $kIdle 'typing.typeChar.p50'; $b95 = Get-G2 $kIdle 'typing.typeChar.p95'
    if ($null -ne $a50 -and $null -ne $b50) {
        $d50 = [math]::Round($a50 - $b50, 2); $d95 = [math]::Round($a95 - $b95, 2)
        & $add 'Indexing never disturbs the IDE' 'Below-normal priority, at most half the cores; paused during build, test, debug and battery saver; keystroke latency unchanged with indexing running' ("TypeChar on the UI thread, indexing running ({0:P0} of status samples busy, {1} rebuilds) p50 {2:0.00} ms, p95 {3:0.00} ms; idle p50 {4:0.00} ms, p95 {5:0.00} ms; difference p50 {6:+0.00;-0.00} ms, p95 {7:+0.00;-0.00} ms; priority, cores and pauses: component tests in step b" -f (Get-G2 $kIdx 'busyFraction'), (Get-G2 $kIdx 'rebuilds'), $a50, $a95, $b50, $b95, $d50, $d95) $(if ($d50 -lt 0.5 -and $d95 -le 2) { 'Pass' } else { 'Fail' }) 'IDE: the Gate 1 typing pass (autocomplete off) while the index is rebuilt continuously, against the same pass with the index idle'
    }
    else { & $add 'Indexing never disturbs the IDE' 'keystroke latency unchanged with indexing running' 'not measured' 'Not measured' '' }

    # Persistence.
    $ph = & $sc 'gate2-persistence-host'
    $rst = $Data['restart']
    $two = $Data['twoInstances']
    $persistOk = (Get-G2 $ph 'afterKill.found') -eq $true -and (Get-G2 $rst 'found') -eq $true -and $two -and $null -ne $two.TakeoverSeconds
    & $add 'Persistence' 'Conversations, pins and the Outbound log survive a host restart and a Visual Studio restart; two hosts share one index with one writer; a stale lock is reclaimed' "host kill: conversation found $(Get-G2 $ph 'afterKill.found') with $(Get-G2 $ph 'afterKill.messages') messages and $(Get-G2 $ph 'afterKill.pins') pin(s), Outbound records of the request $(Get-G2 $ph 'afterKill.outboundRecordsOfTheRequest'); Visual Studio restart: found $(Get-G2 $rst 'found') with $(Get-G2 $rst 'messages') messages; two instances: hosts $(if ($two) { $two.Hosts -join ', ' }), one writer $(if ($two) { $two.Writer }), taken over after $(if ($two) { $two.TakeoverSeconds }) s; integration test ASavedConversation_SurvivesKillingTheHost $(& $outcome 'ASavedConversation_SurvivesKillingTheHost')" $(if ($persistOk) { 'Pass' } elseif ($ph -or $two) { 'Fail' } else { 'Not measured' }) 'IDE scenario with a host kill; a second Visual Studio session; two instances in a private hive with writer.lock read from disk'

    # Encryption.
    $enc = $Data['encryption']
    $encStep = & $step 'e2.'
    & $add 'Encryption at rest' 'index.db and conversations.db unreadable without the DPAPI-protected key; the key file has a user-only ACL; no plain-text chunk text outside the database' "$(Get-G2 $encStep 'details.Summary')" $(if ($encStep) { $encStep.status } else { 'Not measured' }) 'Copies opened with plain SQLite (Python sqlite3); Get-Acl; byte search of the data folder for a sentence of the generated code'

    # Secret files.
    $ix = & $sc 'gate2-index-solution'
    $secretOk = (Get-G2Count (Get-G2 $ix 'secretFiles')) -ge 2 -and ((Get-G2 $ix 'envMention.requiresApproval') -eq $true -or (Get-G2 $ix 'envMention.isSensitive') -eq $true) -and (Get-G2 $ix 'envMention.hasContent') -eq $false
    & $add 'Secret-file exclusion' 'Not indexed, listed in the Index window, per-file approval as a pin' "listed as secret-bearing: $((@(Get-G2 $ix 'secretFiles')) -join ', '); @file .env without approval: requires approval $(Get-G2 $ix 'envMention.requiresApproval'), content returned $(Get-G2 $ix 'envMention.hasContent'); with approval: content $(Get-G2 $ix 'envMentionApproved.hasContent'); search for their text: $((Get-G2Count (Get-G2 $ix 'secretTextSearchHits'))) hits in them; Index window test $(& $outcome 'IndexWindow_ShowsTheRootRow')" $(if ($null -eq $ix) { 'Not measured' } elseif ($secretOk) { 'Pass' } else { 'Fail' }) 'IDE: ListExcludedFilesAsync, ResolveMentionAsync(.env) with and without approval, SearchDetailedAsync'

    # Citations.
    $cite = & $sc 'gate2-chat-cites'
    $citeTest = & $outcome 'CitedAnswer_ShowsTheSpanLink'
    & $add 'Cited answers' 'Replies that used retrieved chunks cite file:line spans; citations open the location' "real host and $((Get-G2 $cite 'model')): $((Get-G2Count (Get-G2 $cite 'citations'))) citations ($(Get-G2 $cite 'explicit') explicit, $(Get-G2 $cite 'implicit') implicit), e.g. $((@(Get-G2 $cite 'citations') | Select-Object -First 2) -join ', '); opening a span: integration test $citeTest" $(if ($null -eq $cite) { 'Not measured' } elseif ((Get-G2Count (Get-G2 $cite 'citations')) -gt 0 -and $citeTest -eq 'Passed') { 'Pass' } else { 'Fail' }) 'IDE: ChatAsync with retrieval on; CitedAnswer_ShowsTheSpanLink_... (fake host, the window)'

    # Autocomplete with retrieval (Gate 1 rows re-run).
    $strong = & $sc 'autocomplete-strong'; $fast = & $sc 'autocomplete-fast'; $off = & $sc 'autocomplete-off'
    $p50 = Get-G2 $strong 'autocomplete.timeToSuggestion.p50'; $p95 = Get-G2 $strong 'autocomplete.timeToSuggestion.p95'
    $k50 = $null; $k95 = $null
    if ($null -ne (Get-G2 $fast 'typing.typeChar.p50') -and $null -ne (Get-G2 $off 'typing.typeChar.p50')) { $k50 = [math]::Round((Get-G2 $fast 'typing.typeChar.p50') - (Get-G2 $off 'typing.typeChar.p50'), 2); $k95 = [math]::Round((Get-G2 $fast 'typing.typeChar.p95') - (Get-G2 $off 'typing.typeChar.p95'), 2) }
    & $add 'Autocomplete prefix stability' 'At most three retrieved snippets before the prefix, unchanged across keystrokes in the same member; the Gate 1 autocomplete rows do not regress' "7B with retrieval on: time to suggestion p50 $(& $ms $p50), p95 $(& $ms $p95); prose $(Get-G2 $strong 'typing.proseProposals'), fenced $(Get-G2 $strong 'typing.fencedProposals'); keystrokes on against off p50 $k50 ms, p95 $k95 ms; prompt-prefix reuse 7B $(Get-G2 $strong 'autocomplete.host.promptPrefixReuseRate') ($(Get-G2 $strong 'autocomplete.host.promptPrefixReuses') of $(Get-G2 $strong 'autocomplete.host.promptPrefixChecks')), 1.5B $(Get-G2 $fast 'autocomplete.host.promptPrefixReuseRate') ($(Get-G2 $fast 'autocomplete.host.promptPrefixReuses') of $(Get-G2 $fast 'autocomplete.host.promptPrefixChecks')); host cache hit rate 7B $(Get-G2 $strong 'autocomplete.host.hitRate'); snippet placement and stability: unit tests (2.12)" $(if ($null -eq $p50) { 'Not measured' } elseif ($p50 -le 300 -and $p95 -le 800 -and ($null -eq $k50 -or ($k50 -lt 0.5 -and $k95 -le 2))) { 'Pass' } else { 'Fail' }) 'Gate 1 typing scenarios in the same IDE run (retrieval on, the scratch solution indexed); IDiagnosticsService counters'

    & $add 'Offline run' 'Every check above with the network adapter disabled' 'tools/gate0/Invoke-OfflineRun.ps1 -Phase 2 written; run by the product owner' 'Pending product owner' 'Elevated, by the product owner'
    $ci = & $step 'g.'
    & $add 'CI' 'Green on develop with the 18.x job and the 17.14 integration-test job' "$(Get-G2 $ci 'details.Summary')" $(if ($ci) { $ci.status } else { 'Not measured' }) 'gh run list / gh run view'
    $flags = & $step 'h.'
    & $add 'Feature flags' 'Every work item behind a flag; tests green' "$(Get-G2 $flags 'details.Summary')" $(if ($flags) { $flags.status } else { 'Not measured' }) 'README flag list; test results'

    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("## Gate 2 criteria ($v)")
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Criterion | Target | Measured | Status | How |')
    [void]$md.AppendLine('| --- | --- | --- | --- | --- |')
    foreach ($r in $rows) { [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} |' -f (($r.criterion, $r.target, $r.measured, $r.status, $r.how) | ForEach-Object { ([string]$_ -replace '\|', '\|' -replace "`r?`n", ' ') }))) }
    [pscustomobject]@{ Rows = $rows.ToArray(); Markdown = $md.ToString() }
}

#endregion

Export-ModuleMember -Function *-*
