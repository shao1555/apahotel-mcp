# apahotel-mcp

アパホテル公式サイトから空室状況と料金を取得する、ローカル用の MCP サーバー（stdio）。

> [!IMPORTANT]
> **非公式**のツールです。アパホテル株式会社およびアパグループとは一切関係がなく、承認や支援も受けていません。
> **無保証**です。取得した空室・料金が正確である保証はなく、予約前には必ず公式サイトで確認してください。
> このツールの利用によって生じたいかなる損害についても、作者は責任を負いません。

- 対象はアパホテルの直営ホテルと FC ホテル。アパ直提携ホテルは対象外。
- 料金はすべて税込で、会員割引は含まない。宿泊税は含まれないことがある。
- 食事条件の既定は素泊まり。各ホテルについて、予約可能な部屋の最安料金を返す。

## ツール

| ツール | 内容 |
|---|---|
| `search_hotels` | ホテル名・住所・エリアでホテルを検索する |
| `list_areas` | 都道府県とエリア（エリア ID）の一覧を返す |
| `get_hotel_prices` | 1 軒について、指定日程の最安料金を返す。`all_plans: true` を付けると全プランと全部屋タイプを返す |
| `search_vacancies` | エリアなどで絞った複数のホテルについて、空室と最安料金を安い順に返す |
| `get_price_calendar` | 1 軒について、1 か月分の日別料金と空室数を返す |

## ビルド

```sh
mise install          # .NET SDK 10
mise run publish      # ./dist/apahotel-mcp（自己完結型の単一バイナリ）を作る
```

`dist/apahotel-mcp` は .NET ランタイムがなくても起動するので、MCP クライアントにはこのバイナリを登録する。
mise で入れた dotnet は `DOTNET_ROOT` を設定しないため、`bin/` 配下の apphost を直接指定すると起動しない。

## 登録

### Claude Code

```sh
claude mcp add apahotel --scope user -- "$PWD/dist/apahotel-mcp"
```

### Codex

```sh
codex mcp add apahotel -- "$PWD/dist/apahotel-mcp"
```

`codex mcp add` を使わない場合は、`~/.codex/config.toml` に直接書いてもよい。

```toml
[mcp_servers.apahotel]
command = "/absolute/path/to/apahotel-mcp/dist/apahotel-mcp"
```

## しくみ

| 用途 | エンドポイント |
|---|---|
| ホテルマスタ | `GET /json/hotel.json`（24 時間キャッシュ） |
| 予約システム側のホテル ID（`econ_hotel_id`） | ホテル詳細ページの HTML から取得し、ファイルに永続キャッシュ |
| 指定日程の全プラン × 部屋タイプの合計料金 | `POST /apacustom/get_planlist.jsp`（HTML） |
| 月間カレンダー（大人 1 名 1 泊あたりの料金と空室数） | `POST /apacustom/apa_hotel_calendar_json.jsp` |

キャッシュは `~/.cache/apahotel-mcp/` に保存する（`XDG_CACHE_HOME` を設定していればそちらに保存する）。

公式サイトのエリア検索は Queue-it の待合室を通る仕組みになっている。このサーバーはその待合室を通らず、ホテル詳細ページが内部で呼んでいるエンドポイントだけを使う。
同時リクエスト数は 4 に制限している。`search_vacancies` は 1 回で最大 `max_hotels`（既定 30）軒に問い合わせる。

## 注意

- 公開 API ではなく、サイト内部のエンドポイントを使っている。サイトが改修されると動かなくなる可能性がある。
- 個人がローカルで使う前提で作っている。短い間隔で繰り返し問い合わせるような使い方はしないこと。
- 公式サイトの利用規約を守るのは利用者の責任とする。

## ライセンス

[MIT License](LICENSE)
