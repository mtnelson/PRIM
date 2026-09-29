"""Interactive browser test for RipDesk (Blazor Server) at http://127.0.0.1:5123/."""
import sys, time
from playwright.sync_api import sync_playwright

BASE = "http://203.0.113.1:5123"
results = []
console_errors = []
page_errors = []

def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + (f" — {detail}" if detail else ""), flush=True)

with sync_playwright() as p:
    browser = p.chromium.launch(executable_path="/opt/meta-chromium/chrome",
                                args=["--no-sandbox",
                                      "--disable-features=LocalNetworkAccessChecks"])
    page = browser.new_page(viewport={"width": 1600, "height": 900})
    page.on("console", lambda m: console_errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: page_errors.append(str(e)))

    # 1. Load + Blazor circuit connects
    page.goto(BASE, wait_until="domcontentloaded")
    try:
        page.wait_for_selector("nav", timeout=15000)
        # Blazor error UI should stay hidden
        err_visible = page.evaluate(
            "() => { const e = document.getElementById('blazor-error-ui');"
            " return e ? getComputedStyle(e).display !== 'none' : false; }")
        check("blazor circuit connects (no error UI)", not err_visible)
    except Exception as ex:
        check("blazor circuit connects (no error UI)", False, str(ex)[:120])

    # 2. Navigate each section via the nav menu, verify content renders
    sections = ["Dashboard", "Records", "Containers", "Locations", "Users",
                "Advanced Search", "Workspaces", "Reports", "Administration"]
    for s in sections:
        try:
            page.get_by_role("link", name=s).first.click()
            page.wait_for_timeout(1200)
            body_text = page.evaluate("() => document.body.innerText")
            ok = len(body_text.strip()) > 200 and "An unhandled error" not in body_text
            check(f"nav: {s} renders", ok, f"{len(body_text.strip())} chars")
        except Exception as ex:
            check(f"nav: {s} renders", False, str(ex)[:120])

    # 3. Records: right-click row -> context menu; left-click -> view pane details
    try:
        page.get_by_role("link", name="Records").first.click()
        page.wait_for_timeout(1500)
        row = page.locator("tbody tr").first
        row.click(button="right")
        page.wait_for_timeout(800)
        menu_visible = page.evaluate(
            "() => !!document.querySelector('.mud-menu-popover, .mud-popover-open')")
        check("records right-click opens context menu", menu_visible)
        page.keyboard.press("Escape")
        page.wait_for_timeout(400)
        row.click()
        page.wait_for_timeout(1000)
        body_text = page.evaluate("() => document.body.innerText")
        check("records row click shows details in view pane",
              "Record Number" in body_text or "Barcode" in body_text)
    except Exception as ex:
        check("records row interactions", False, str(ex)[:150])

    # 4. New Record dialog opens and cancels
    try:
        btn = page.get_by_role("button", name="New Record").first
        btn.click()
        page.wait_for_timeout(1200)
        dlg = page.locator(".mud-dialog")
        opened = dlg.count() > 0 and dlg.first.is_visible()
        check("new record dialog opens", opened)
        if opened:
            page.get_by_role("button", name="Cancel").first.click()
            page.wait_for_timeout(800)
            closed = page.locator(".mud-dialog").count() == 0
            check("new record dialog cancels", closed)
    except Exception as ex:
        check("new record dialog open/cancel", False, str(ex)[:150])

    # 5. Dashboard quick search
    try:
        page.get_by_role("link", name="Dashboard").first.click()
        page.wait_for_timeout(1200)
        search = page.locator("input").first
        search.fill("HQ")
        page.wait_for_timeout(1500)
        body_text = page.evaluate("() => document.body.innerText")
        check("dashboard quick search returns results",
              "HQ" in body_text and len(body_text.strip()) > 500)
    except Exception as ex:
        check("dashboard quick search", False, str(ex)[:150])

    # 6. Top bar: user/role switcher + announcement area
    try:
        body_text = page.evaluate("() => document.body.innerText")
        has_user = page.locator("header").count() > 0
        check("top bar renders", has_user)
    except Exception as ex:
        check("top bar renders", False, str(ex)[:120])

    browser.close()

print("\n--- console errors ---")
for e in console_errors[:10]:
    print("CONSOLE:", e[:200])
print("--- page errors ---")
for e in page_errors[:10]:
    print("PAGEERROR:", e[:200])

failed = [r for r in results if not r[1]]
print(f"\n--- {len(results)-len(failed)} passed, {len(failed)} failed ---")
sys.exit(1 if failed else 0)
