import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

export default defineConfig({
  base: "/slipmat-companion/",
  build: {
    target: "baseline-widely-available",
    outDir: "dist",
    emptyOutDir: true,
    rolldownOptions: {
      input: {
        overview: fileURLToPath(new URL("./index.html", import.meta.url)),
        onboarding: fileURLToPath(new URL("./onboarding/index.html", import.meta.url)),
        story: fileURLToPath(new URL("./story/index.html", import.meta.url)),
      },
    },
  },
});
