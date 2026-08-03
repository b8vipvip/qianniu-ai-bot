# Client Business Policy JSON

Business-specific phrases and workflow text are stored at:

```text
%LocalAppData%\QianniuAiBot\data\business-policy.json
```

The verified release package contains `default-business-policy.json`. On first launch, the Bot copies it to the persistent data directory. The user file survives program updates.

Open:

```text
知识库 → 店铺规则中心 → 运行策略JSON
```

The editor supports import, export, formatting, default restore, regex validation, automatic backup and live reload. A successful save becomes active within about two seconds without restarting the Bot.

## Sections

- `patterns`: deterministic message and answer regexes.
- `stages`: conversation stage names and next actions.
- `facts`: privacy-safe facts supplied to the reply state.
- `buyerGoals`: resolved intent for short contextual messages.
- `prompts`: high-priority workflow boundaries supplied to the model.
- `validationIssues`: pre-send validation messages.
- `handoffOverrides`: business exceptions applied before the remote handoff compatibility layer.

## Safety boundary

Only merchant-specific business knowledge and workflow wording belongs in this JSON. Cross-buyer send protection, authentication, token secrecy, message deduplication, file validation, privacy redaction, updater integrity checks and other engineering safety controls remain in code.

Invalid JSON or regex is rejected before save. The previous file is backed up under the persistent data `backups` directory.

## Regression cases

The default policy requires:

```text
电视端的有吗 能登自己账号吗
```

This is a normal own-account capability question unless it also mentions password, verification code, recovery, theft, freeze, ban, real-name identity or other account-security risk.

For:

```text
那拍哪个链接啊
```

when the prior seller turn requested a photo or screenshot, the intent is the TV-page target. The answer must clarify that the buyer should not photograph a product link, and should instead photograph the TV's Kugou account/binding page or, if unavailable, the membership purchase page.
