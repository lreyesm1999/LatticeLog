"""Captures the web console screenshots in docs/assets/screenshots (see that folder's README for the full recipe).

    python -I scripts/screenshots/capture_console.py "<url printed by `latticelog ui --no-open`>" docs/assets/screenshots

Needs the Python package `playwright` and a locally installed Microsoft Edge (channel="msedge"); no browser is downloaded.
"""
import sys
from playwright.sync_api import sync_playwright
url, out = sys.argv[1], sys.argv[2]
with sync_playwright() as p:
    b = p.chromium.launch(channel="msedge", headless=True, args=["--lang=en-US", "--accept-lang=en-US"])
    ctx = b.new_context(viewport={"width": 1440, "height": 860}, locale="en-US", timezone_id="UTC",
                        color_scheme="dark", device_scale_factor=1.5, reduced_motion="reduce")
    pg = ctx.new_page()
    pg.goto(url); pg.wait_for_timeout(2500)
    pg.screenshot(path=f"{out}/console-sources.png")
    pg.get_by_text("checkout-legacy", exact=True).first.dblclick()
    pg.wait_for_selector("text=matching events"); pg.wait_for_timeout(1500)
    pg.locator("select").filter(has_text="All time").first.select_option(label="All time")
    pg.locator("#query button[type=submit]").click()
    pg.wait_for_timeout(2500)
    pg.screenshot(path=f"{out}/console-search.png")
    pg.get_by_role("button", name="Errors", exact=True).first.click()
    pg.wait_for_timeout(2500)
    pg.screenshot(path=f"{out}/console-errors.png")
    print(pg.evaluate("navigator.language"), pg.evaluate("new Intl.NumberFormat().format(53617)"))
    b.close()
