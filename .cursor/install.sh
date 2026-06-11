#!/usr/bin/env bash
# Cloud Agent environment setup: .NET 8 SDK + cTrader.Automate package
# so cAlgo cBot files in this repo can be compile-checked with `dotnet build`.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Install the .NET 8 SDK into ~/.dotnet (the installer is a no-op if the
# requested version is already present there).
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

# Make dotnet available in future shells.
if ! grep -q 'DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.bashrc" 2>/dev/null; then
  {
    echo 'export DOTNET_ROOT="$HOME/.dotnet"'
    echo 'export PATH="$DOTNET_ROOT:$PATH"'
  } >> "$HOME/.bashrc"
fi

# Pre-restore NuGet packages (cTrader.Automate) for the compile-check project.
dotnet restore "$REPO_ROOT/DualEdgeRhythmFVG.csproj"

dotnet --version
echo "Environment ready: run 'dotnet build' in the repo root to compile-check cBots."
