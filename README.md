# SQL Server 互換性レベルクエリチェッカー

SQL Server のデータベース互換性レベルを上げる前に、T-SQL の構文を
[Microsoft.SqlServer.TransactSql.ScriptDom](https://github.com/microsoft/SqlScriptDOM)
で比較するコマンドラインツールです。

解析は常にオフラインで行います。データベース内のストアドプロシージャ、SQL関数、トリガー、
クエリキャッシュを調べる場合も、まず `export` モードで `.sql` ファイルへ保存し、
その後 `analyze` モードで保存先を解析します。

> [!IMPORTANT]
> このツールが確認するのは ScriptDOM で Parse できる構文かどうかだけです。
> 実行結果、オブジェクトの存在、動的 SQL、実行計画、カーディナリティ推定、性能の変化は検証しません。
> 本番環境の互換性を保証するものではないため、実環境でのテストも実施してください。

## 必要環境

- .NET 10 SDK または対応する .NET 10 Runtime
- エクスポートする場合は SQL Server 2012 以降への接続環境
- Windows、Linux、macOS のいずれか

`analyze` の実行時には SQL Server やネットワーク接続は不要です。ただし、ツールのインストールや
ソースからの初回ビルドでは、NuGet パッケージを未取得の場合にネットワーク接続が必要です。

主な実行環境と依存パッケージは次のバージョンに固定しています。

| 項目 | バージョン |
|---|---:|
| .NET | 10.0（LTS） |
| Microsoft.SqlServer.TransactSql.ScriptDom | 180.59.2 |
| Microsoft.Data.SqlClient | 7.0.2 |
| System.CommandLine | 2.0.11 |

## インストール

NuGet で公開されたパッケージは次のコマンドでグローバルツールとしてインストールできます。

```console
dotnet tool install --global MSSQLCompatibilityLevelQueryChecker.Tool
```

リポジトリのソースからローカルパッケージを作成して試す場合は、リポジトリのルートで次を実行します。

```console
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet pack src/MssqlCompatCheck/MssqlCompatCheck.csproj --configuration Release --no-build --output ./artifacts
dotnet tool install --global --add-source ./artifacts MSSQLCompatibilityLevelQueryChecker.Tool --version 1.0.0
```

同じバージョンが既にインストールされている場合は、一度アンインストールしてから再インストールします。

```console
dotnet tool uninstall --global MSSQLCompatibilityLevelQueryChecker.Tool
```

## オプションの短縮名

すべての公開オプションには、1文字の短縮名があります。短縮名の大文字と小文字は区別されます。

| 短縮名 | 正式名 | 用途 |
|---|---|---|
| `-m` | `--mode` | `analyze` または `export` |
| `-c` | `--current-level` | 現在の互換性レベル |
| `-t` | `--target-level` | 変更先の互換性レベル |
| `-L` | `--level-scope` | 解析範囲（`range` または `target`） |
| `-s` | `--sql-directory` | 解析する SQL ディレクトリ（複数指定可） |
| `-o` | `--output` | 出力ディレクトリ |
| `-d` | `--database` | エクスポート対象データベース |
| `-M` | `--include-modules` | `P` / `FN` / `IF` / `TR` / `V` のSQLモジュールをエクスポート |
| `-Q` | `--include-query-cache` | クエリキャッシュをエクスポート |
| `-e` | `--connection-string-env` | 接続文字列を格納した環境変数名 |
| `-E` | `--encoding` | BOM なし SQL の文字コード |
| `-i` | `--quoted-identifiers` | 通常 SQL の `QUOTED_IDENTIFIER` 設定 |
| `-f` | `--include-full-sql` | レポートへ SQL 全文を含める |
| `-u` | `--ignore-unexpected-eof` | Unexpected EOFエラー（46029）を除外 |
| `-w` | `--overwrite` | 既存のツール出力を上書き |
| `-v` | `--version` | バージョン情報を表示 |

ヘルプには System.CommandLine 標準の `-h`（`--help`）を使用できます。

たとえば、オフライン解析は短縮名だけでも実行できます。

```console
mssql-compat-check -m analyze -c 110 -t 170 -s ./exported-queries -o ./reports/analysis
```

エクスポートの短縮形は次のとおりです。

```console
mssql-compat-check -m export -d ApplicationDb -M -Q -e MSSQL_COMPAT_CONNECTION_STRING -o ./exported-queries
```

## オフライン解析

`--mode analyze` は、指定したディレクトリにある `.sql` ファイルだけを読み取ります。

```console
mssql-compat-check \
  --mode analyze \
  --current-level 110 \
  --target-level 170 \
  --sql-directory ./exported-queries \
  --output ./reports/analysis
```

PowerShell では1行で実行するか、継続文字をバッククォートへ置き換えてください。

- `--current-level`、`--target-level`、`--sql-directory`、`--output` は必須です。
- `--target-level` には `--current-level` より大きい値を指定します。
- 対応レベルは ScriptDOM が通常の SQL Server 用パーサーを生成できる 80～180 です。
- `--current-level` は現在の互換性レベルです。変更前の状態を確認するため内部でParseしますが、互換性レベル別のサマリーやタブには含めません。
- 既定の `--level-scope range` では、現在レベルより大きく変更先レベル以下の、ScriptDOMが対応するすべての互換性レベルで個別にParseします。たとえば110から170を指定すると、120、130、140、150、160、170がレポート対象です。
- `--level-scope target` を指定すると、変更先の互換性レベルだけでParseします。短縮名は `-L target` です。
- `.sql` の拡張子は大文字・小文字を区別しません。
- 指定ディレクトリ直下だけでなく、深さに制限なく全サブディレクトリを再帰探索します。この動作を無効にするオプションはありません。
- 親ディレクトリとその子ディレクトリを同時に指定しても、同じ実体ファイルは1回だけ解析します。
- シンボリックリンクや Windows のジャンクションは辿りません。
- `--sql-directory` を繰り返すことで、複数のディレクトリを指定できます。
- データベース接続用のオプションを `analyze` と組み合わせることはできません。

解析中は、SQLファイルの探索、対象件数、解析進捗、レポート生成状況をコンソールへ表示します。
大量のファイルを解析する場合、進捗は約5%刻みで表示されます。

```text
SQLファイルを探索しています...
解析対象: 3773 ファイル、6 互換性レベル
解析中: 1/3773 ファイル (0.0%)
解析中: 189/3773 ファイル (5.0%)
...
解析中: 3773/3773 ファイル (100.0%)
SQL解析が完了しました。
レポートを生成しています...
```

クエリキャッシュから切り出された不完全な制御フロー文など、ScriptDOMのUnexpected EOFエラーを
判定対象から除外する場合は`--ignore-unexpected-eof`（`-u`）を指定します。

```console
mssql-compat-check -m analyze -c 120 -t 180 -s ./exported-queries -o ./reports/analysis -u
```

このオプションが除外するのはScriptDOMのエラー番号`46029`だけです。同じSQLに別のParseエラーが
ある場合は失敗として残ります。不完全なSQLを互換と扱う可能性があるため、既定では無効です。
オプションの有効状態はHTMLとJSONの両方へ記録されます。

変更先の互換性レベルだけを確認する例:

```console
mssql-compat-check -m analyze -c 110 -t 170 -L target -s ./sql -o ./reports/target-only
```

`--level-scope` を省略した場合は `range` となり、現在レベルの次に対応するレベルから変更先レベルまでを段階的に確認します。現在レベルのParse結果は、変更前から存在する問題と変更後に発生する問題の分類にだけ利用します。

通常の SQL ファイルでは、必要に応じて Parse 時の設定を指定できます。

```console
mssql-compat-check --mode analyze --current-level 120 --target-level 160 \
  --sql-directory ./sql --encoding shift_jis --quoted-identifiers false \
  --output ./reports/analysis
```

エクスポートされた SQL については、隣接する `manifest.json` の `QUOTED_IDENTIFIER` と
ソース情報を利用します。マニフェストがない通常のディレクトリも解析できます。

## クエリのエクスポート

`--mode export` だけが SQL Server へ接続します。モジュールとクエリキャッシュの両方を
エクスポートする例は次のとおりです。

```console
mssql-compat-check \
  --mode export \
  --database ApplicationDb \
  --include-modules \
  --include-query-cache \
  --connection-string-env MSSQL_COMPAT_CONNECTION_STRING \
  --output ./exported-queries
```

- `--database` と `--output` は必須です。
- `--include-modules` または `--include-query-cache` の少なくとも一方が必要です。
- `--include-modules` は `sys.sql_modules` と `sys.objects` から、ユーザー作成オブジェクトのうち `type` が `P`、`FN`、`IF`、`TR`、`V` のものを対象にします。
- `--include-query-cache` は対象データベースに帰属できる、取得可能なすべての完了済みキャッシュステートメントを対象にします。
- `export` はファイルの保存だけを行い、互換性解析や HTML レポート生成は行いません。
- `--current-level`、`--target-level`、`--level-scope`、`--sql-directory`、`--encoding`、`--quoted-identifiers`、`--include-full-sql`、`--ignore-unexpected-eof` は `export` では指定できません。

エクスポート先は入力元ごとのサブディレクトリへ分かれます。指定しなかった入力元の
サブディレクトリは作成されません。

```text
exported-queries/
├── .mssql-compat-output
├── export-manifest.json
├── modules/
│   ├── manifest.json
│   ├── P/
│   │   ├── manifest.json
│   │   └── procedure-dbo-ProcessOrder-123.sql
│   ├── FN/
│   │   ├── manifest.json
│   │   └── scalar-function-dbo-CalculateTax-124.sql
│   ├── IF/
│   │   ├── manifest.json
│   │   └── inline-table-function-dbo-GetOrders-125.sql
│   ├── TR/
│   │   ├── manifest.json
│   │   └── trigger-dbo-OrderTrigger-126.sql
│   └── V/
│       ├── manifest.json
│       └── view-dbo-OrderView-127.sql
└── cache/
    ├── manifest.json
    ├── <sha256>.sql
    └── <sha256>.sql
```

暗号化されて定義を取得できないモジュールは `.sql` を作成せず、スキップ理由を
マニフェストへ記録します。SQL ファイルは UTF-8 で保存されます。

モジュールのtypeと出力先は次の対応です。該当するモジュールがないtypeのディレクトリは作成しません。

| `sys.objects.type` | 対象 | 出力先 |
|---|---|---|
| `P` | SQLストアドプロシージャ | `modules/P` |
| `FN` | SQLスカラー関数 | `modules/FN` |
| `IF` | SQLインラインテーブル値関数 | `modules/IF` |
| `TR` | SQLトリガー | `modules/TR` |
| `V` | ビュー | `modules/V` |

`modules/manifest.json`には全typeの項目をまとめて記録し、各typeディレクトリの`manifest.json`にはそのtypeの項目だけを記録します。
`TF`（複数ステートメントのテーブル値関数）、CLRモジュール、データベーススコープDDLトリガーなど、上表以外のtypeは対象外です。

### 接続文字列

接続文字列そのものはコマンドラインへ渡さず、環境変数へ設定します。既定の環境変数名は
`MSSQL_COMPAT_CONNECTION_STRING` です。別名を使う場合だけ `--connection-string-env` を指定します。

PowerShell の例:

```powershell
$env:MSSQL_COMPAT_CONNECTION_STRING = 'Server=localhost;Database=master;Integrated Security=true;Encrypt=true'
mssql-compat-check --mode export --database ApplicationDb --include-modules --output ./exported-queries
```

Bash の例:

```bash
export MSSQL_COMPAT_CONNECTION_STRING='Server=localhost;Database=master;Integrated Security=true;Encrypt=true'
mssql-compat-check --mode export --database ApplicationDb --include-modules --output ./exported-queries
```

接続文字列はコンソール、マニフェスト、レポートへ出力しません。SQL 認証を使う場合も、
パスワードを引数やスクリプト、ソース管理対象ファイルへ直接書かないでください。

### SQL Server の権限

- モジュールの定義をすべて取得するには、対象データベースへの接続権限とメタデータを参照できる権限が必要です。必要に応じて対象データベースの `VIEW DEFINITION` を付与してください。
- クエリキャッシュの取得には、SQL Server 2019 以前では `VIEW SERVER STATE`、SQL Server 2022 以降では `VIEW SERVER PERFORMANCE STATE` が必要です。
- 必要最小限の権限を持つ専用アカウントを使用してください。

収集処理でSQL Server例外以外の予期しない問題が発生した場合、`Database collection failed before it could be completed`に安全な例外種別を付加して出力します。例外メッセージそのものは接続情報などを含む可能性があるため、マニフェストやコンソールには記録しません。
エクスポートが途中で失敗した後、同じ出力先へ再実行する場合は`--overwrite`（`-w`）を指定してください。

## エンコーディング

解析時は BOM 付き UTF-8／UTF-16 を自動判定します。BOM のないファイルは、既定では
不正なバイトを許容しない UTF-8 として読み取ります。Shift-JIS などを扱う場合は
`--encoding shift_jis` のように .NET が認識できるエンコーディング名を明示してください。
読み取れないファイルは処理メッセージに記録され、読み取り可能なファイルの処理は継続されます。

## 出力、レポート、上書き

解析結果は `--output` で指定したディレクトリに同時生成されます。

```text
reports/analysis/
├── analysis-report.json
└── analysis-report.html
```

- JSON の `schemaVersion` は `1.0` です。
- HTMLヘッダーとJSONの`scriptDomVersion`には、解析に使用したScriptDOMのNuGetパッケージバージョンを記録します。
- JSON の `levelScope` に解析範囲を、`analyzedLevels`、`levelSummaries`、各ファイルの `levelResults` にレポート対象となる互換性レベル別の結果を記録します。現在の互換性レベルはこれらの配列に含めません。
- 現在レベルのParse結果は各ファイルの比較情報として保持しますが、現在レベルだけのParse失敗はレベル別の失敗件数および終了コード`1`の判定には含めません。
- HTML は外部 JavaScript や CSS に依存しない単一ファイルです。
- HTMLはダッシュボード形式で、画面幅に応じてヘッダー、互換性レベル別集計、エラー別の該当ファイル表示を再配置します。印刷用スタイルも同じファイルに含みます。
- HTML の先頭には互換性レベルごとの対象件数、Parse成功件数、Parse失敗件数を一覧できるサマリーテーブルを表示します。
- サマリーには互換性レベルと「エラー番号＋エラーメッセージ」ごとの発生件数、該当ファイル数も表示します。同じ互換性レベルのセルは縦方向に結合します。同じファイル内で同じエラーが複数回返された場合、発生件数と該当ファイル数は異なることがあります。
- HTML の詳細は互換性レベルごとのタブ表示です。タブ名の括弧内にはそのレベルでParseに失敗したファイル数を表示します。各タブには失敗したファイルだけを表示し、成功したファイルは件数だけを表示します。JSONには全ファイル・全レベルの結果を記録します。
- 各互換性レベルの詳細は、エラー番号とメッセージごとにまとめて表示します。エラーごとに発生件数、該当ファイル数、該当ファイルへのリンク、行・列・オフセットを確認できます。行・列・オフセットは3桁区切りで表示します。長いファイルパスは区切り文字および一定文字数ごとに改行可能な状態で表示します。
- HTMLのファイルパスは元の`.sql`ファイルを開くローカルファイルリンクです。ブラウザーのセキュリティ設定によっては`file://`リンクが制限される場合があります。
- 既定のレポートは SQL 本文全体を含まず、エラー周辺の最大3行・500文字だけをHTMLへ表示します。SHA-256はHTMLには表示せず、機械処理や追跡に利用できるようJSONだけに記録します。
- SQL 本文全体が必要な場合だけ `--include-full-sql` を指定してください。
- 既存の出力は既定で上書きしません。意図して置き換える場合だけ `--overwrite` を指定してください。

### エクスポート出力の安全マーカー

`.mssql-compat-output` は、`export --overwrite` 実行時の誤削除を防ぐために本ツールが作成する内部マーカーです。
上書き時は、このファイルが存在し、内容が期待値と一致する場合に限り、既存の出力ディレクトリを置き換えます。

- `analyze` モードでは使用しません。SQL の解析結果にも影響しません。
- SQL 本文、接続文字列、データベース情報などは含みません。
- エクスポート開始時に作成するため、処理が途中で中断された場合も `--overwrite` で安全に再実行できます。
- マーカーを削除または変更すると、そのディレクトリに対する `--overwrite` は安全のため失敗します。
- 任意の既存ディレクトリを削除する用途には使えません。

## 終了コード

| コード | `analyze` | `export` |
|---:|---|---|
| `0` | 全ファイルが選択した解析範囲の全互換性レベルでParse成功 | 要求した入力元のエクスポート完了 |
| `1` | いずれかのファイルが選択した解析範囲の1つ以上の互換性レベルでParse失敗 | 暗号化モジュールなど、一部項目をスキップ |
| `2` | 引数、探索、読取、マニフェスト、レポート生成のエラー、または対象 SQL が0件 | 引数、接続、権限、収集、ファイル出力のエラー |
| `130` | ユーザーによるキャンセル | ユーザーによるキャンセル |

途中で問題が発生しても、可能な場合は成功分と処理メッセージを含むレポートまたはマニフェストを生成します。
CI では終了コードとレポートの両方を確認してください。

## セキュリティとプライバシー

- エクスポートされた `.sql` には、モジュール定義や実際に実行されたクエリ文字列がそのまま保存されます。リテラル、個人情報、業務情報、シークレットを含む可能性があります。
- クエリハッシュや既定のレポート抜粋にも機密情報が残る場合があります。レポートの共有範囲にも注意してください。
- エクスポート先とレポート先には適切なアクセス制御を設定し、不要になったファイルは組織の規程に従って処分してください。
- `--include-full-sql` は出力先の保護と共有範囲を確認した場合だけ使用してください。
- 接続先が信頼する証明書を使う構成を推奨します。証明書検証を無効化する設定を本番環境で安易に使わないでください。

## サンプル

[`samples`](samples) には、再帰探索と Parse 結果を確認するための小さな SQL ファイルがあります。

```console
mssql-compat-check --mode analyze --current-level 120 --target-level 130 \
  --sql-directory ./samples --output ./reports/samples
```

- `basic` と入れ子になった `nested/modules` は通常の有効な SQL の例です。
- `version-sensitive` は ScriptDOM のパーサーバージョンによる差を観察するための構文例です。採用する ScriptDOM バージョンで生成された実際のレポートを判断材料にしてください。
- `invalid` は対象となる全互換性レベルでParseエラーになることを意図したファイルです。そのため、上記の実行は終了コード `1` になります。

## ライセンス

このリポジトリは [MIT License](LICENSE) の下で提供されます。
