from pathlib import Path

path = Path("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
text = path.read_text(encoding="utf-8")
old = '''            catch (OperationCanceledException)\n            {\n                throw;\n            }\n            catch (Exception ex)\n            {\n                aiWatch.Stop();\n                return await ContinueAfterAiFailureAsync(\n'''
new = '''            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)\n            {\n                aiWatch.Stop();\n                return await ContinueAfterAiFailureAsync(\n                    shop,\n                    seller,\n                    "阶段1/6 API网络：通过\\n"\n                    + "阶段2/6 Token/ShopKey：通过\\n"\n                    + "阶段3/6 Control Plane 路由：已进入\\n"\n                    + "阶段4/6 上游供应商/模型调用：超时\\n"\n                    + "阶段5/6 AI回复文本解析：未执行\\n"\n                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\\n"\n                    + "错误：" + Safe(ex.Message, 1600),\n                    overall,\n                    cancellationToken);\n            }\n            catch (OperationCanceledException)\n            {\n                // Only an explicit caller/user cancellation is allowed to stop the diagnostic.\n                throw;\n            }\n            catch (Exception ex)\n            {\n                aiWatch.Stop();\n                return await ContinueAfterAiFailureAsync(\n'''
# There are two OperationCanceledException blocks in this class; only the one immediately
# followed by the AI continuation catch has this exact suffix.
if text.count(old) != 1:
    raise SystemExit(f"expected exactly one AI cancellation block, got {text.count(old)}")
path.write_text(text.replace(old, new), encoding="utf-8")
print("diagnostic HTTP timeout continuation applied")
