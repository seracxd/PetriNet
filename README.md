# Petri Net Analyzer

[![CI/CD](https://github.com/seracxd/PetriNet/actions/workflows/deploy.yml/badge.svg)](https://github.com/seracxd/PetriNet/actions/workflows/deploy.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-220%20passing-brightgreen)](PetriEditor.Tests)
[![Coverage](https://img.shields.io/badge/coverage-81.3%25%20(analysis%20core)-brightgreen)](docs/coverage)

A browser-based interactive editor, simulator, and formal analyzer for Petri
nets. Built with Blazor Auto (WebAssembly + Server) on .NET 10. All editing
and simulation happen client-side in the browser; computationally heavy formal
analysis can be offloaded to the server over SignalR.

> Academic project (semestrální práce, VŠB-TUO FEI).
> See [`docs/Dokumentace.pdf`](docs/Dokumentace.pdf) for the full technical
> documentation and [`docs/coverage/`](docs/coverage) for the test-coverage
> report.

---

## Features

### Editor

- **Place, Transition, Arc** primitives with bipartite-graph validation
- **Three arc types**: normal, inhibitor, reset
- **Full undo / redo** via command pattern; composite commands undo atomically
- **Pan, zoom, box-select**, element navigator panel
- **Click-to-connect** arc drawing with floating tip; endpoint drag-to-reconnect
- **Snapshot/restore** properties editing (no flicker, atomic undo)
- **Runtime settings**: grid size, arc colors, node sizes, firing delay
- **In-app debug log** with ring buffer, configurable severity

### Simulation

- Interactive **step-by-step** firing with priority-aware enabled-set selection
- **Auto-fire** with adjustable delay
- **Time-travel**: jump to any historical step, return to present
- **Reset** to initial marking

### Formal analysis

Five independent analysis engines and seven property tests, all decoupled
from the UI:

| Engine | Algorithm |
|---|---|
| State Space / Coverability tree | Karp–Miller (BFS, ω-acceleration) |
| Reachability tree | Plain BFS with cycle detection |
| Invariants | Farkas / support enumeration on incidence matrix |
| Classification | Structural predicates (State Machine, Marked Graph, Free Choice, EFC) |
| Cycles | Johnson's algorithm (1975), elementary circuits |
| Traps & co-traps (siphons) | Minimal subset enumeration |

Property tests: **liveness, boundedness, safety, conservativeness,
repetitiveness, deadlock-freedom, reversibility**.

Each analysis result includes diagnostic reasons (`PropertyTestResult`),
respects special-arc semantics (inhibitor / reset disable ω-acceleration),
and is capped by global limits (max nodes, max candidates, 60 s wall-clock
deadline).

### Operations

- **PDF export** of analysis report (server-side via QuestPDF)
- **SVG export** of coverability tree and graph
- **Highlight** any cycle, invariant, or trap directly in the net
- **Chunked transfer** of large coverability graphs (200 nodes/SignalR message)

---

## Architecture

The solution is a modular monolith of five .NET projects:

```
PetriEditor.Shared      → domain models, DTO contracts (PnPlace, Arc, …)
PetriEditor.Analysis    → algorithms, simulator (no UI / web dependency)
PetriEditor.Client      → Blazor WebAssembly UI
PetriEditor.Server      → ASP.NET Core host, SignalR hub, PDF export
PetriEditor.Tests       → xUnit test suite (220 cases)
```

```
┌─────────────────────────────────────────────────────┐
│                    Web browser                      │
└─────────────┬─────────────────────────────┬─────────┘
              │ HTTPS / WASM                │ HTTPS
              ▼                             ▼
┌─────────────────────┐  SignalR  ┌────────────────────┐
│  PetriEditor.Client │ ◀───────▶ │  PetriEditor.Server │
│  (Blazor WASM)      │  (JSON)   │  (ASP.NET Core 10)  │
└──────────┬──────────┘           └──────────┬──────────┘
           │                                 │
           ▼                                 ▼
        ┌────────────────────────────────────────┐
        │       PetriEditor.Analysis             │
        │       (engines + simulator)            │
        └─────────────────┬──────────────────────┘
                          ▼
                ┌──────────────────────┐
                │  PetriEditor.Shared  │
                │  (models, contracts) │
                └──────────────────────┘
```

`PetriEditor.Analysis` is a pure domain library with **no UI or web
dependency**, which lets the same algorithm code run client-side (WASM, small
nets) or server-side (large nets, heavy analysis). See
[`docs/Dokumentace.pdf`](docs/Dokumentace.pdf) §2 for full diagrams and
deployment view.

---

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: [Docker](https://docs.docker.com/get-docker/) for containerised
  runs

### Run from source

```bash
git clone https://github.com/seracxd/PetriNet.git
cd PetriNet
dotnet run --project PetriEditor.Server
```

Open <http://localhost:5000>.

### Run with Docker

```bash
docker build -t petrinet .
docker run -d -p 8080:8080 --name petrinet petrinet
```

Open <http://localhost:8080>.

### Run tests

```bash
dotnet test
```

All 220 test cases finish in roughly 2 seconds. To collect coverage:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

The Cobertura XML output is consumed by
[ReportGenerator](https://github.com/danielpalme/ReportGenerator) to produce
the HTML report in [`docs/coverage/`](docs/coverage).

---

## Continuous deployment

Pushes to `master` trigger
[`.github/workflows/deploy.yml`](.github/workflows/deploy.yml):

1. **Build & test** on a GitHub-hosted Ubuntu runner
   - Restores, builds, and runs the full xUnit suite inside the Docker build
   - Publishes the image to GitHub Container Registry
     (`ghcr.io/seracxd/petrinet:latest`)
2. **Deploy** on a self-hosted runner on the target server
   - Pulls the freshly built image
   - Stops and replaces the running container
   - Restart policy `unless-stopped` keeps it up across reboots

If any of the 220 tests fail, the image is not built and the deploy step is
skipped — the previous container keeps running.

---

## Project status & limitations

- **No persistence** — diagrams live in memory and are lost on browser
  refresh. A persistence layer is planned but not implemented.
- **No timed Petri nets**; transition priorities are supported.
- **Coverability tree** is capped at 50 000 nodes (Karp–Miller).
  Inhibitor/reset arcs disable ω-acceleration (they break monotonicity),
  so for special-arc nets the tree is bounded by a tighter cap.
- **UI test coverage is low** by design — the editor relies on manual E2E
  testing in Chrome and Firefox; algorithmic code is unit-tested.

See [`TODO.md`](TODO.md) for the running engineering log (analysis-correctness
fixes, performance tuning, code-review findings, etc.).

---

## Documentation

| Document | What's inside |
|---|---|
| [`docs/Dokumentace.pdf`](docs/Dokumentace.pdf) | Final project documentation (CZ): architecture, testing, configuration management |
| [`docs/coverage/index.html`](docs/coverage/index.html) | Interactive coverage report (line / branch / method per class) |
| [`docs/diagrams.drawio`](docs/diagrams.drawio) | Architecture and deployment diagrams in draw.io format |
| [`TODO.md`](TODO.md) | Engineering log: improvements, code-review findings, known issues |

---

## Tech stack

- **.NET 10**, C# 12
- **ASP.NET Core 10** — server, antiforgery, DataProtection
- **Blazor WebAssembly + Blazor Server** (interactive auto render mode)
- **SignalR 9** — bidirectional client ↔ server analysis, chunked graph
  streaming
- **[Z.Blazor.Diagrams 3.0.4](https://blazor-diagrams.zhaytam.com/)** —
  diagram rendering
- **QuestPDF** — server-side PDF report generation
- **xUnit 2.9** + **coverlet** — testing and coverage
- **Docker** — multi-stage build (SDK → aspnet runtime, ~200 MB final image)

---

## Acknowledgements

- Petri-net theory: Murata, T. (1989). *Petri nets: Properties, analysis and
  applications.* Proceedings of the IEEE, 77(4).
- Cycle enumeration: Johnson, D. B. (1975). *Finding all the elementary
  circuits of a directed graph.* SIAM J. Comput., 4(1).
- Built as a semestrální práce (semester project) at
  [VŠB-TUO FEI](https://www.fei.vsb.cz/).

---

## License

[MIT](LICENSE) © 2026 David Šigut
