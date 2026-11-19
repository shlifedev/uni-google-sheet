// Publishes the engine (dotnet/UGS.Desktop.Host) as a self-contained single file and puts it where Tauri looks for
// the sidecar: src-tauri/binaries/ugs-host-<target triple>[.exe]. Runs before `tauri dev` and `tauri build`.
//
// The .NET SDK is taken from $DOTNET, then the macOS installer location (Homebrew's `dotnet` may be an older SDK),
// then PATH. The target comes from TAURI_ENV_TARGET_TRIPLE (set by the Tauri CLI) or the Rust host triple.
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, chmodSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const desktop = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repo = resolve(desktop, "..");
const project = join(repo, "dotnet", "UGS.Desktop.Host", "UGS.Desktop.Host.csproj");

const rids = {
  "aarch64-apple-darwin": "osx-arm64",
  "x86_64-apple-darwin": "osx-x64",
  "x86_64-pc-windows-msvc": "win-x64",
  "aarch64-pc-windows-msvc": "win-arm64",
  "x86_64-unknown-linux-gnu": "linux-x64",
  "aarch64-unknown-linux-gnu": "linux-arm64",
};

function hostTriple() {
  const output = execFileSync("rustc", ["-vV"], { encoding: "utf8" });
  const line = output.split("\n").find((entry) => entry.startsWith("host:"));
  if (!line) throw new Error("Could not read the Rust host triple from `rustc -vV`.");
  return line.slice("host:".length).trim();
}

function dotnet() {
  if (process.env.DOTNET) return process.env.DOTNET;
  const installer = "/usr/local/share/dotnet/dotnet";
  return existsSync(installer) ? installer : "dotnet";
}

const triple = process.env.TAURI_ENV_TARGET_TRIPLE || hostTriple();
const rid = rids[triple];
if (!rid) throw new Error(`No .NET runtime identifier is known for the target '${triple}'.`);

// Not EnableCompressionInSingleFile: with it the published host crashed (AccessViolationException inside
// System.Text.Json) on the second request, every time, on .NET 10.0.12 / osx-arm64. The installers compress anyway.
const windows = triple.includes("windows");
const out = join(desktop, "src-tauri", "target", "ugs-host", rid);
console.log(`[build-host] publishing ugs-host for ${rid} (${triple})`);
execFileSync(
  dotnet(),
  ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true", "-o", out, "--nologo", "-v", "quiet"],
  { stdio: "inherit" },
);

const binaries = join(desktop, "src-tauri", "binaries");
mkdirSync(binaries, { recursive: true });
const source = join(out, windows ? "ugs-host.exe" : "ugs-host");
const target = join(binaries, `ugs-host-${triple}${windows ? ".exe" : ""}`);
copyFileSync(source, target);
if (!windows) chmodSync(target, 0o755);
console.log(`[build-host] ${target}`);
