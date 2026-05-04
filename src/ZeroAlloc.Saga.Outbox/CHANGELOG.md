# Changelog

## [1.5.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga.Outbox-v1.4.0...ZeroAlloc.Saga.Outbox-v1.5.0) (2026-05-04)


### Features

* **saga:** introduce ISagaUnitOfWork abstraction (Phase 3a-2 stage 1) ([#29](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/29)) ([39ef0e3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/39ef0e3f6d370b66760a805e007af88574b8b475))

## [1.4.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga.Outbox-v1.3.0...ZeroAlloc.Saga.Outbox-v1.4.0) (2026-05-04)


### Features

* **generator:** rename Add{Saga}Saga() → With{Saga}Saga() with [Obsolete] shim ([#27](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/27)) ([879f6c1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/879f6c17266a565812070ee8d7dd46d20de811e8))

## [1.3.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga.Outbox-v1.2.0...ZeroAlloc.Saga.Outbox-v1.3.0) (2026-05-04)


### Features

* **samples:** add AotSmokeOutbox sample to verify the AOT-rooting contract ([#23](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/23)) ([25ed3fd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/25ed3fd1ffc3b5de48c686b5aaeadf08cb17352c))
* scope-per-attempt retry loop — exactly-once dispatch under OCC retry ([#25](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/25)) ([37b043a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/37b043aacbd507879a204b409d400f01a0205e1f))


### Bug Fixes

* **outbox:** switch to UnconditionalSuppressMessage on reflective lookups ([37b043a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/37b043aacbd507879a204b409d400f01a0205e1f))
* **outbox:** switch to UnconditionalSuppressMessage on reflective lookups ([25ed3fd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/25ed3fd1ffc3b5de48c686b5aaeadf08cb17352c))

## 1.2.0 (2026-05-04)


### Features

* phase 3a — outbox bridge for atomic command dispatch ([#21](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/21)) ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))


### Documentation

* **outbox:** tighten atomicity wording, fix DI lifetime + model-builder name ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))


### Tests

* **outbox:** reframe Test 2 docstring to describe what it proves (and doesn't) ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))


### Miscellaneous

* **release:** tag v1.1.0 ([7ac8399](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/7ac83990fc6eac0bab2fc022c9a26100154d1edf))
