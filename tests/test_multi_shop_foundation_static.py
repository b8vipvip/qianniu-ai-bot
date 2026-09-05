import hashlib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_identity_prefers_target_id_and_marks_nickname_fallback_unstable():
    source = read("src/Bot/ShopScope/ShopIdentityResolver.cs")
    assert "seller.TargetId" in source
    assert 'sellerIdentity = "nick:"' in source
    assert "hasStableSellerId = true" in source
    assert "hasStableSellerId = false" in source
    assert "seller.Display" in source
    assert "seller.Nick" in source


def test_shop_key_is_deterministic_and_not_based_on_display_name():
    source = read("src/Bot/ShopScope/ShopKeyGenerator.cs")
    context = read("src/Bot/ShopScope/ShopContext.cs")
    expected = "qn_" + hashlib.sha256(b"qianniu:seller-123").hexdigest()[:12]
    assert expected == "qn_a0ae1c2e9052"
    assert 'var canonical = platform + ":" + sellerIdentity' in source
    assert "SHA256.Create" in source
    assert "DigestCharacters = 12" in source
    assert "DisplayName is informational only" in context


def test_profile_store_preserves_shop_key_when_display_name_changes():
    source = read("src/Bot/ShopScope/ShopProfileStore.cs")
    assert "SameIdentity(x, shop)" in source
    assert "existing.DisplayName = shop.DisplayName" in source
    assert "existing.ShopKey, shop.ShopKey" in source
    assert "ShopKey collision detected" in source
    assert "duplicate seller identity" in source
    assert "AtomicWrite" in source
    assert 'RegistrySchema = "qnbot.shop-registry"' in source


def test_scoped_path_provider_creates_global_shop_and_compatibility_boundaries():
    source = read("src/Bot/ShopScope/ShopScopedPathProvider.cs")
    interface = read("src/Bot/ShopScope/IShopScopedPathProvider.cs")
    for value in (
        'Path.Combine(UserDataRoot, "global")',
        'Path.Combine(UserDataRoot, "shops")',
        'Path.Combine(GlobalRoot, "shops.json")',
        'Path.Combine(ShopsRoot, shop.ShopKey)',
        'GetShopDirectory(shop, "config")',
        'GetShopDirectory(shop, "knowledge")',
        'GetShopDirectory(shop, "rules")',
        'GetShopDirectory(shop, "state")',
        'GetShopDirectory(shop, "cache")',
        'GetShopDirectory(shop, "logs")',
        'GetShopDirectory(shop, "backup")',
        'Path.Combine(GetStateRoot(shop), "data")',
    ):
        assert value in source
    assert "LegacyDataRoot" in source
    assert "LegacyDataRoot" in interface
    assert "GetCompatibilityDataRoot" in source
    assert "GetCompatibilityDataRoot" in interface
    assert "PathEx.GlobalDataDir" in source
    assert "Path.IsPathRooted" in source
    assert "Path.GetInvalidFileNameChars" in source


def test_foundation_is_compiled_for_bot_and_wpf_temporary_projects():
    props = read("src/Bot/Directory.Build.props")
    for filename in (
        "IShopScopedPathProvider.cs",
        "ShopContext.cs",
        "ShopIdentityResolver.cs",
        "ShopKeyGenerator.cs",
        "ShopProfile.cs",
        "ShopProfileStore.cs",
        "ShopScopedPathProvider.cs",
    ):
        assert "ShopScope\\" + filename in props


def test_foundation_remains_credential_free_while_runtime_bridge_owns_storage_switch():
    foundation = "\n".join(
        read(path)
        for path in (
            "src/Bot/ShopScope/IShopScopedPathProvider.cs",
            "src/Bot/ShopScope/ShopContext.cs",
            "src/Bot/ShopScope/ShopIdentityResolver.cs",
            "src/Bot/ShopScope/ShopKeyGenerator.cs",
            "src/Bot/ShopScope/ShopProfile.cs",
            "src/Bot/ShopScope/ShopProfileStore.cs",
            "src/Bot/ShopScope/ShopScopedPathProvider.cs",
        )
    )
    path_ex = read("src/BotLib/Extensions/PathEx.cs")
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    assert "ControlPlaneClientToken" not in foundation
    assert "ProtectedData" not in foundation
    assert "TrySaveParam" not in foundation
    assert "PathEx.GlobalDataDir" in foundation
    assert "LegacyDataRoot" in foundation
    assert "ScopedDataPathRouter.TryResolve" in path_ex
    assert "GetCompatibilityDataRoot" in bridge
