import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const read = (path) => readFile(new URL(path, import.meta.url), "utf8");

test("presentation site uses Vite 8+ without Astro or esbuild", async () => {
  const [packageJson, config] = await Promise.all([
    read("../package.json"),
    read("../vite.config.js"),
  ]);
  const pkg = JSON.parse(packageJson);

  assert.match(pkg.devDependencies?.vite ?? "", /^\^?8\./);
  assert.equal(pkg.dependencies?.astro, undefined);
  assert.equal(pkg.devDependencies?.astro, undefined);
  assert.equal(pkg.dependencies?.esbuild, undefined);
  assert.equal(pkg.devDependencies?.esbuild, undefined);
  assert.equal(pkg.scripts?.build, "vite build");
  assert.match(config, /rolldownOptions/);
  assert.match(config, /onboarding/);
  assert.match(config, /story/);
});

test("all pages are semantic Vite entries with native navigation transitions", async () => {
  const [overview, onboarding, story, css] = await Promise.all([
    read("../index.html"),
    read("../onboarding/index.html"),
    read("../story/index.html"),
    read("../src/styles/global.css"),
  ]);

  for (const html of [overview, onboarding, story]) {
    assert.match(html, /data-site-generator="vite"/);
    assert.match(html, /<main id="content">/);
    assert.match(html, /<script type="module" src="\\/src\\/site\\.js"><\\/script>/);
    assert.match(html, /rel="icon" href="%BASE_URL%favicon\\.svg"/);
  }
  assert.match(css, /@view-transition/);
  assert.match(css, /navigation:\s*auto/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);
  assert.match(css, /view-transition-name:\s*slipmat-companion-brand/);
});

test("onboarding keeps browser-local checklist semantics explicit", async () => {
  const [page, behavior] = await Promise.all([
    read("../onboarding/index.html"),
    read("../src/site.js"),
  ]);
  assert.match(page, /data-checklist/);
  assert.match(page, /These controls do not configure Jellyfin or send data anywhere/);
  assert.match(behavior, /localStorage/);
  assert.match(behavior, /slipmat-companion-onboarding-v1/);
});
