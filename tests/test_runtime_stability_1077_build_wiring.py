from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_delivery_verification_partial_is_compiled_by_legacy_wpf_projects():
    targets = (ROOT / "src/Directory.Build.targets").read_text(encoding="utf-8-sig")
    source = ROOT / "src/Bot/ChromeNs/QN.DeliveryVerification.cs"

    assert source.is_file()
    assert "QN.DeliveryVerification.cs" in targets
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.DeliveryVerification.cs" />' in targets
