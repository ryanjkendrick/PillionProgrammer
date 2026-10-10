# Gate 1 report

Phase 1 closes at Gate 1 ([phase-1-plan.md](phase-1-plan.md)): "autocomplete without retrieval and chat within budget,
online and offline" (design document section 17). This report collects the evidence for each criterion, says how it was
measured and lists what only the product owner can do. Work item 1.18.

Date: 2026-10-10. Machine: the reference machine (Ryzen 7 3700X, 8 cores and 16 threads, 32 GB, RTX 2080 SUPER 8 GB,
driver 617.42, Windows 11 Pro 26300); Visual Studio 2026 18.10.3 and Visual Studio 2022 17.14.41 with IntelliCode; Ollama
0.40.1 with `qwen2.5-coder:7b` (digest `dae161e27b0e`, Q4_K_M), `qwen2.5-coder:1.5b` (`d7372fd82851`, Q4_K_M) and
`gemma4:e2b`. Code: branch `feature/gate1-evidence`, which is `develop` at `4845f06` (every phase 1 work item through
PR 39) plus the scripts and tests of this work item. VSIX 0.1.0, Pillion Host 0.1.0, contract 1.2. No cloud key is stored on
this machine (`cmdkey /list:PillionProgrammer/*` lists none), so the cloud rows are pending.

RUNS_PLACEHOLDER
