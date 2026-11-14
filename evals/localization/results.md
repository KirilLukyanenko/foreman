10 cases, map budget 3000 tokens. Recall@k: share of the fix's source files in the top k files, averaged over cases. Hit@5: at least one of them in the top 5. MRR: 1 / rank of the best-placed one. In map: share shown in the rendered map.

| Method | Recall@1 | Recall@3 | Recall@5 | Recall@10 | Hit@5 | MRR | In map |
|---|---|---|---|---|---|---|---|
| bm25 | 20% | 40% | 60% | 65% | 70% | 0.38 |  |
| map, no task | 10% | 20% | 30% | 40% | 30% | 0.19 | 70% |
| map + exact mentions | 10% | 20% | 40% | 40% | 40% | 0.22 | 60% |
| map + task words | 10% | 30% | 40% | 40% | 40% | 0.21 | 60% |
| map seeded by bm25 | 10% | 20% | 40% | 50% | 40% | 0.21 | 60% |
| bm25 + map (RRF) | 20% | 35% | 60% | 65% | 70% | 0.36 |  |
| task reader | 65% | 90% | 90% | 90% | 100% | 0.90 |  |
| task reader, then bm25 | 65% | 90% | 90% | 90% | 100% | 0.90 |  |

Rank of each gold file per method (— = not ranked):

| Case | Lang | Files | bm25 | map, no task | map + exact mentions | map + task words | map seeded by bm25 | bm25 + map (RRF) | task reader | task reader, then bm25 |
|---|---|---|---|---|---|---|---|---|---|---|
| [ardalis-GuardClauses-339](https://github.com/ardalis/GuardClauses/issues/339) | C# | 54 | 1 | 24 | 20 | 12 | 8 | 1 | 1 | 1 |
| [App-vNext-Polly-2815](https://github.com/App-vNext/Polly/issues/2815) | C# | 791 | 27, 20 | 479, 389 | 52, 43 | 143, 146 | 60, 35 | 24, 12 | 1, 2 | 1, 2 |
| [FluentValidation-FluentValidation-2240](https://github.com/FluentValidation/FluentValidation/issues/2240) | C# | 215 | 8, 3 | 38, 52 | 45, 50 | 41, 28 | 19, 17 | 8, 4 | 1, 2 | 1, 2 |
| [jbogard-MediatR-1118](https://github.com/jbogard/MediatR/issues/1118) | C# | 149 | 2, 63 | 18, 67 | 12, 90 | 11, 89 | 11, 92 | 3, 81 | 1, — | 1, 63 |
| [unjs-defu-119](https://github.com/unjs/defu/issues/119) | TypeScript | 6 | 4 | 3 | 2 | 3 | 3 | 3 | 1 | 1 |
| [honojs-hono-4325](https://github.com/honojs/hono/issues/4325) | TypeScript | 374 | 64, 101 | 26, 105 | 48, 69 | 31, 63 | 40, 82 | 38, 85 | 2, — | 2, 101 |
| [pallets-itsdangerous-375](https://github.com/pallets/itsdangerous/issues/375) | Python | 15 | 4 | 4 | 4 | 5 | 4 | 4 | 1 | 1 |
| [python-humanize-humanize-239](https://github.com/python-humanize/humanize/issues/239) | Python | 12 | 3 | 6 | 5 | 3 | 5 | 4 | 1 | 1 |
| [Textualize-rich-3577](https://github.com/Textualize/rich/issues/3577) | Python | 213 | 33 | 38 | 47 | 42 | 58 | 40 | 2 | 2 |
| [spf13-cobra-2240](https://github.com/spf13/cobra/issues/2240) | Go | 36 | 1 | 1 | 1 | 1 | 1 | 1 | 1 | 1 |
