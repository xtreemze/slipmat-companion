import { defineConfig } from "astro/config";

export default defineConfig({
  site: "https://xtreemze.github.io",
  base: "/slipmat-companion",
  output: "static",
  build: { format: "directory" },
});
