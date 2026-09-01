from pathlib import Path
import subprocess


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("missing patch anchor: " + label)
    return text.replace(old, new, 1)


# Do not let the one-off patch modify a protected GitHub Actions workflow. Packaging
# belongs to the OCR project itself: every win-x64 publish becomes self-contained with
# the native MSVC dependencies imported by onnxruntime.dll.
subprocess.run(
    ["git", "checkout", "HEAD", "--", ".github/workflows/windows-build.yml"],
    check=True,
)

csproj_path = "tools/LocalOcrWorker/LocalOcrWorker.csproj"
csproj = read(csproj_path)
anchor = "  <ItemGroup>\n    <PackageReference Include=\"RapidOcrNet\" Version=\"4.0.2\" />\n  </ItemGroup>\n"
insert = anchor + '''
  <ItemGroup Condition="'$(RuntimeIdentifier)' == 'win-x64'">
    <VcRuntimeDependency Include="$(SystemRoot)\\System32\\vcruntime140.dll" />
    <VcRuntimeDependency Include="$(SystemRoot)\\System32\\vcruntime140_1.dll" />
    <VcRuntimeDependency Include="$(SystemRoot)\\System32\\msvcp140.dll" />
    <VcRuntimeDependency Include="$(SystemRoot)\\System32\\msvcp140_1.dll" />
  </ItemGroup>

  <Target Name="CopyOnnxVcRuntimeDependencies" AfterTargets="Publish" Condition="'$(RuntimeIdentifier)' == 'win-x64'">
    <Error Condition="!Exists('$(SystemRoot)\\System32\\vcruntime140.dll')" Text="Missing vcruntime140.dll required by ONNX Runtime." />
    <Error Condition="!Exists('$(SystemRoot)\\System32\\vcruntime140_1.dll')" Text="Missing vcruntime140_1.dll required by ONNX Runtime." />
    <Error Condition="!Exists('$(SystemRoot)\\System32\\msvcp140.dll')" Text="Missing msvcp140.dll required by ONNX Runtime." />
    <Error Condition="!Exists('$(SystemRoot)\\System32\\msvcp140_1.dll')" Text="Missing msvcp140_1.dll required by ONNX Runtime." />
    <Copy SourceFiles="@(VcRuntimeDependency)" DestinationFolder="$(PublishDir)" SkipUnchangedFiles="true" />
  </Target>
'''
csproj = replace_once(csproj, anchor, insert, "OCR worker package group")
write(csproj_path, csproj)

# Repoint the new regression from the protected workflow to the actual publish project.
test_path = "tests/test_runtime_1095_context_order_ocr_static.py"
test = read(test_path)
start = test.index("def test_ocr_release_bundles_onnx_and_vc_runtime_next_to_worker():")
new_test = '''def test_ocr_release_bundles_onnx_vc_runtime_dependencies_at_publish():
    project = read("tools/LocalOcrWorker/LocalOcrWorker.csproj")
    assert 'AfterTargets="Publish"' in project
    assert 'CopyOnnxVcRuntimeDependencies' in project
    assert 'vcruntime140.dll' in project
    assert 'vcruntime140_1.dll' in project
    assert 'msvcp140.dll' in project
    assert 'msvcp140_1.dll' in project
    assert 'SourceFiles="@(VcRuntimeDependency)"' in project
    assert 'DestinationFolder="$(PublishDir)"' in project
'''
test = test[:start] + new_test
write(test_path, test)
