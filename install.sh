#!/bin/sh
# Installs the latest Treering for Linux (x86-64).
#
#   curl -fsSL https://raw.githubusercontent.com/RedholeTechnologies/Teering/main/install.sh | sh
#
# It goes to ~/.local/bin (or $TREERING_DIR). Nothing needs root. Run it again to update.
set -eu

base="https://github.com/RedholeTechnologies/Teering/releases/latest/download"
dir="${TREERING_DIR:-$HOME/.local/bin}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) ;;
  *) echo "There is a Treering build for Linux on x86-64 only (this is $(uname -s) $(uname -m))." >&2; exit 1 ;;
esac

temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

echo "Downloading Treering..."
curl -fsSL "$base/treering-linux-x64.tar.gz" -o "$temp/treering-linux-x64.tar.gz"
curl -fsSL "$base/SHA256SUMS" -o "$temp/SHA256SUMS"

# The download is checked against the checksum published with the release.
(cd "$temp" && grep ' treering-linux-x64.tar.gz$' SHA256SUMS | sha256sum -c - > /dev/null) || {
  echo "The download does not match its checksum. Try again." >&2
  exit 1
}

tar -xzf "$temp/treering-linux-x64.tar.gz" -C "$temp"
mkdir -p "$dir"
install -m 755 "$temp/treering" "$dir/treering"

echo
echo "Treering is installed in $dir."
case ":$PATH:" in
  *":$dir:"*) echo 'Run "treering" - it opens in your browser.' ;;
  *) echo "Add $dir to your PATH, then run \"treering\" - it opens in your browser." ;;
esac
