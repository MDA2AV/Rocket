-- The ".br case": request the precompressed brotli variant of each asset directly
-- (ioxide.file serves by path and does not content-negotiate). Mirrors what a
-- br-negotiating server would return: .br for the 15 compressible assets, identity
-- for the 5 already-compressed binaries (woff2 fonts, webp images have no .br).
local paths = {
  "/static/reset.css.br",
  "/static/layout.css.br",
  "/static/theme.css.br",
  "/static/components.css.br",
  "/static/utilities.css.br",
  "/static/analytics.js.br",
  "/static/helpers.js.br",
  "/static/app.js.br",
  "/static/vendor.js.br",
  "/static/router.js.br",
  "/static/header.html.br",
  "/static/footer.html.br",
  "/static/regular.woff2",
  "/static/bold.woff2",
  "/static/logo.svg.br",
  "/static/icon-sprite.svg.br",
  "/static/hero.webp",
  "/static/thumb1.webp",
  "/static/thumb2.webp",
  "/static/manifest.json.br",
}

local ae_header = "br;q=1, gzip;q=0.8"
local counter = 0

request = function()
  counter = counter + 1
  local path = paths[((counter - 1) % 20) + 1]
  return wrk.format("GET", path, {["Accept-Encoding"] = ae_header})
end
