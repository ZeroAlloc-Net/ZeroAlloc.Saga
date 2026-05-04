# Changelog

## [1.3.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga.EfCore-v1.2.0...ZeroAlloc.Saga.EfCore-v1.3.0) (2026-05-04)


### Features

* **generator:** rename Add{Saga}Saga() → With{Saga}Saga() with [Obsolete] shim ([#27](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/27)) ([879f6c1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/879f6c17266a565812070ee8d7dd46d20de811e8))

## [1.2.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/ZeroAlloc.Saga.EfCore-v1.1.0...ZeroAlloc.Saga.EfCore-v1.2.0) (2026-04-29)


### Features

* **saga.efcore:** full implementation + comprehensive test suite ([781f134](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/781f1342d392aa6727ed5ead5d5d91f43603e55f))
* **saga.efcore:** implement EfCoreSagaStore&lt;TSaga, TKey&gt; ([19b4b86](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/19b4b8652d2170f7069c5894c3f7c75864559432))


### Bug Fixes

* **efcore:** guard against ordering footgun in WithEfCoreStore&lt;TContext&gt;() ([b7c1737](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/b7c1737aa4844ce9b1f37e8956d95e24e4eb2b17))


### Code Refactoring

* **efcore:** single-source SagaRetryOptions via factory ([c6a84e3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/c6a84e34a72e024734ccacab907dc2658c6845ae))


### Documentation

* **efcore:** correct stale RowVersion mapping note in EfCoreSagaStore ([844d55a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/844d55a83642db201f6d6c7f7aab90747603bf5e))
* **efcore:** explain ChangeTracker cache hit on SaveAsync's GetEntityAsync ([e7a7280](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/e7a7280c176cb2f55359f255cff65e18323f3994))


### Tests

* **saga.efcore:** forward-path tests (6) ([9c035fa](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/9c035fa607b70ca5345f03c00be552452de52238))

## 1.1.0 (2026-04-29)


### Features

* **saga.efcore:** scaffold csproj + multi-package release-please config ([85df40a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/85df40ad3fdfbead3399f8da370b0ba8ef4acd00))
* **saga.efcore:** stub source files (real model config, NotImplementedException stores) ([b479ecd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/b479ecd152210fc40e14856143dad8db95c295d6))


### Miscellaneous

* **release:** tag v1.1.0 ([7ac8399](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/7ac83990fc6eac0bab2fc022c9a26100154d1edf))
