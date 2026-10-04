import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";

const root = resolve("dist");
const basePath = "/slipmat-companion/";
const deploymentProvided = new Set([basePath + "manifest.json"]);
const failures = [];
const external = new Set();

function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? walk(path) : [path];
  });
}

function pagePath(file) {
  const rel = relative(root, file).split(sep).join("/");
  if (rel === "index.html") return basePath;
  if (rel.endsWith("/index.html")) return basePath + rel.slice(0, -"index.html".length);
  return basePath + rel;
}

function localFileFor(pathname) {
  if (!pathname.startsWith(basePath)) return null;
  const rel = pathname.slice(basePath.length);
  if (rel === "") return join(root, "index.html");
  if (pathname.endsWith("/")) return join(root, rel, "index.html");
  return join(root, rel);
}

function hasId(file, id) {
  if (!id || !existsSync(file) || !file.endsWith(".html")) return true;
  const html = readFileSync(file, "utf8");
  return html.includes('id="' + id.replaceAll('"', "&quot;") + '"')
    || html.includes("id='" + id.replaceAll("'", "&#39;") + "'");
}

if (!existsSync(root)) throw new Error("site/dist does not exist; build the site first");

const htmlFiles = walk(root).filter((file) => file.endsWith(".html"));
for (const file of htmlFiles) {
  const html = readFileSync(file, "utf8");
  const from = pagePath(file);

  for (const match of html.matchAll(/\bhref=(["'])(.*?)\1/g)) {
    const href = match[2];
    if (!href || href.startsWith("mailto:") || href.startsWith("tel:")) continue;

    if (/^https?:\/\//.test(href)) {
      try { external.add(new URL(href).href); }
      catch { failures.push(from + ": invalid external URL " + href); }
      continue;
    }

    const url = new URL(href, "https://example.invalid" + from);
    if (url.origin !== "https://example.invalid") continue;
    if (deploymentProvided.has(url.pathname)) continue;

    const target = localFileFor(url.pathname);
    if (!target || !existsSync(target)) {
      failures.push(from + ": missing target " + href);
      continue;
    }
    if (url.hash && !hasId(target, decodeURIComponent(url.hash.slice(1)))) {
      failures.push(from + ": missing fragment target " + href);
    }
  }
}

if (failures.length) {
  console.error("Broken generated links:\n" + failures.map((item) => " - " + item).join("\n"));
  process.exit(1);
}

console.log("Checked " + htmlFiles.length + " generated HTML files; internal links and fragments are valid.");
console.log("External URLs parsed: " + external.size + ". Network reachability is audited separately.");
