# Changelog

## [1.6.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.5.0...ZeroAlloc.Saga-v1.6.0) (2026-05-04)


### Features

* **saga.outbox.redis:** new package — atomic outbox under Redis (Phase 3a-2 stage 3) ([#32](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/32)) ([5eb950b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/5eb950b1ca538a7fd05d1c8d6c9baba1b5ae3f72))
* **saga.redis:** new package — Redis-backed durable persistence (Phase 3a-2 stage 2) ([#31](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/31)) ([393d05b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/393d05ba2cde9ba367b67184de015d36163bf6cb))
* **saga:** introduce ISagaUnitOfWork abstraction (Phase 3a-2 stage 1) ([#29](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/29)) ([39ef0e3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/39ef0e3f6d370b66760a805e007af88574b8b475))

## [1.5.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.4.0...ZeroAlloc.Saga-v1.5.0) (2026-05-04)


### Features

* **generator:** rename Add{Saga}Saga() → With{Saga}Saga() with [Obsolete] shim ([#27](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/27)) ([879f6c1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/879f6c17266a565812070ee8d7dd46d20de811e8))

## [1.4.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.3.0...ZeroAlloc.Saga-v1.4.0) (2026-05-04)


### Features

* phase 3a — outbox bridge for atomic command dispatch ([#21](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/21)) ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))


### Documentation

* **outbox:** tighten atomicity wording, fix DI lifetime + model-builder name ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))


### Tests

* **outbox:** reframe Test 2 docstring to describe what it proves (and doesn't) ([1a567dc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1a567dc5951738b093310d64ec4c60354f01bdd8))

## [1.3.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.2.0...ZeroAlloc.Saga-v1.3.0) (2026-05-01)


### Features

* lock public API surface (PublicApiAnalyzers + api-compat gate) ([#17](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/17)) ([4f870c4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/4f870c48e01226fbe4d4788da8b206e34db95b96))

## [1.2.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.1.0...ZeroAlloc.Saga-v1.2.0) (2026-04-29)


### Features

* **saga.efcore:** full implementation + comprehensive test suite ([781f134](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/781f1342d392aa6727ed5ead5d5d91f43603e55f))
* **saga:** typed registrar + backend-agnostic retry options ([c737002](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/c737002e530436f426f8cf1ab77da7855fc97777))

## [1.1.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga-v1.1.0...ZeroAlloc.Saga-v1.1.0) (2026-04-29)


### Features

* add ISagaBuilder + AddSaga() DI extension ([cce4e12](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/cce4e129981ea481d64a04c54f8429866dd47f63))
* add ISagaManager + SagaManager + ISagaCompensationDispatcher ([3ea663f](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/3ea663fac7f8b9e1119e7bd9fffd588430e84a03))
* add ISagaStore&lt;TSaga,TKey&gt; + InMemorySagaStore default ([42f2dba](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/42f2dba935370d76bfa4cd54b64bd2001eaefb5f))
* add public Saga, Step, CorrelationKey attributes ([8386152](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/83861522d35e23d877c29486a845d0a6189ca2b7))
* add SagaLockManager&lt;TKey&gt; for per-saga serialization ([0b8ca8a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/0b8ca8a4c9588570de9e7a4d3e7b7d4acfcf8c4c))
* runtime + source generator + happy-path tests ([19647f2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/19647f23cc0f0a7b03042c6f9d9b6e54e8e0e666))
* **saga.efcore:** scaffold csproj + multi-package release-please config ([85df40a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/85df40ad3fdfbead3399f8da370b0ba8ef4acd00))
* **saga:** add [NotSagaState] escape-hatch attribute ([bb5d2e1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/bb5d2e10edc85a814bc0062caac2593c03406b10))
* **saga:** add ISagaPersistableState interface for backend state round-trip ([8ae2400](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/8ae240076429954b49fea70672878c771b7144a7))
* **saga:** add SagaStateReader ref struct mirroring writer ([a077b9a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/a077b9aaf250e34fc7c46a043a57c424a3497303))
* **saga:** add SagaStateVersionMismatchException + SagaConcurrencyException ([e1bc7f8](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/e1bc7f8e6c198c509d27f0e8b7b9813c9e39df80))
* **saga:** add SagaStateWriter ref struct for state serialization ([683b7d5](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/683b7d5949a9c700c97dcdbc3458e15db878c7bf))
* **saga:** expose IsEfCoreBackend flag + SagaStoreRegistrar indirection ([f3383d0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/f3383d036ec0a69c7eb8fa9d153e7861407e571d))
* **saga:** v1.1 — ISagaPersistableState, byte serializer, ZASAGA014/015 ([5ac16a4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/5ac16a4013f9034e887a113a983f7e9f268ac3c1))


### Bug Fixes

* **saga:** drop framework-convention auxiliary ctors on sealed exceptions ([ca45925](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/ca45925f47fd9920ac9c8d29bc0094737e87b0d6))
* **saga:** preserve null-vs-empty round-trip for byte[]? state fields ([489e5bc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/489e5bc9f931eafc64121fa95149d2c58104217d))


### Miscellaneous

* **release:** tag v1.1.0 ([7ac8399](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/7ac83990fc6eac0bab2fc022c9a26100154d1edf))
