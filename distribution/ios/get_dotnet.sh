#!/bin/bash

# XCCONFIG_FILE="${SRCROOT}/MeloNX.xcconfig"

# CI / custom setups can pin the exact dotnet binary via MELIORA_DOTNET.
if [ -n "${MELIORA_DOTNET:-}" ] && [ -x "$MELIORA_DOTNET" ]; then
    echo "$MELIORA_DOTNET"
    exit 0
fi

SEARCH_PATHS=(
    "/usr/local/share/dotnet"
    "/usr/local/bin"
    "/usr/bin"
    "/bin"
    "/opt"
    "/Library/Frameworks"
    "$HOME/.dotnet"
    "$HOME/dotnet"
    "$HOME/Developer"
    "~/.dotnet"
    "~/dotnet"
    "~/Developer"
)



DOTNET_PATH=""

for path in "${SEARCH_PATHS[@]}"; do
    if [ -d "$path" ]; then
        DOTNET_PATH=$(find "$path" -name dotnet -type f -print -quit 2>/dev/null)
        if [ -n "$DOTNET_PATH" ]; then
            break
        fi
    fi
done

if [ -z "$DOTNET_PATH" ]; then
    exit 1
fi

echo "$DOTNET_PATH"
