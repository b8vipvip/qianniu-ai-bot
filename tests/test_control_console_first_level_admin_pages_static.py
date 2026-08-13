from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STATIC = ROOT / "services" / "api-control-plane" / "static"


def test_admin_utility_pages_are_promoted_into_primary_console_navigation():
    index = (STATIC / "index.html").read_text(encoding="utf-8")
    shell = (STATIC / "console-sections.js").read_text(encoding="utf-8")
    loader = (STATIC / "client-token-copy.js").read_text(encoding="utf-8")

    assert 'href="/static/wecom.html"' in index
    assert 'href="/static/recharge-query.html"' in index
    assert 'script.src="/static/console-sections.js?v=1"' in loader

    assert 'makePrimaryButton(document.querySelector(\'a.nav[href="/static/wecom.html"]\'),"wecom")' in shell
    assert 'makePrimaryButton(document.querySelector(\'a.nav[href="/static/recharge-query.html"]\'),"recharge-query")' in shell
    assert 'button.dataset.page=page' in shell
    assert 'section.id=`page-${page}`' in shell
    assert 'frame.src=config.src' in shell


def test_embedded_admin_pages_keep_parent_sidebar_and_hide_legacy_secondary_shell():
    shell = (STATIC / "console-sections.js").read_text(encoding="utf-8")

    assert 'if(aside)aside.style.display="none"' in shell
    assert 'if(header)header.style.display="none"' in shell
    assert 'history.replaceState({page},"",routeUrl(page))' in shell
    assert 'new URLSearchParams(location.search).get("page")' in shell
    assert 'a.nav:not([data-page])' in shell
    assert 'link.onclick=null' in shell


def test_embedded_pages_are_lazy_loaded_instead_of_loading_on_every_dashboard_visit():
    shell = (STATIC / "console-sections.js").read_text(encoding="utf-8")

    assert 'if(!frame.dataset.loaded)' in shell
    assert 'frame.dataset.loaded="1"' in shell
    assert 'wecom:{frameId:"wecomFrame",src:"/static/wecom.html?embedded=1"}' in shell
    assert '"recharge-query":{frameId:"rechargeQueryFrame",src:"/static/recharge-query.html?embedded=1"}' in shell
