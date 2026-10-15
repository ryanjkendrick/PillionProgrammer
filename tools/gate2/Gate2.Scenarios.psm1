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
    param([Parameter(Mandatory)][string]$Root, [int]$Files = 600, [int]$LinesPerFile = 170, [int]$BranchFiles = 500)
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

Export-ModuleMember -Function *-*
