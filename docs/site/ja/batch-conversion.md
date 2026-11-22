---
title: 一括コンバーターとビルド自動化
---

# 一括コンバーターとビルド自動化

配布物の **Converter** フォルダーを使い、LIBERA FX の GUI を起動せず変換できます。
出力するのはエフェクトのパッケージで、Unity／Unreal／Godot のインポート・
割り当て・ゲームビルドは別工程です。[制作フロー](production-workflow.md)も参照してください。

Spawnable Group・CPU／GPU Particle・Fluid2Dを、エディターと同じ実行用パッケージへ変換します。
ネストしたスコープにも対応します。コンバーターとエンジンSDKは同じビルドを使ってください。
Resource Identity登録済み文書は、通常の`.lfxb`一括出力ではなく末尾の診断用経路が対象です。

<details class="page-toc" markdown="1">
<summary>このページの内容</summary>

* TOC
{:toc}

</details>

## 単体変換とフォルダー一括変換

以下は Windows PowerShell で、配布物の `Converter` フォルダーから実行する例です。
コンバーターと SDK は同じビルドを使います。

```powershell
.\LiberaFxConverter.exe --help
.\LiberaFxConverter.exe convert "C:\FX\Source\Spark.lfxproj" "C:\FX\Build\Spark.lfxb"
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\FX\Build"
```

`batch` は入力以下の `.lfxproj` を再帰的に探し、相対階層を維持して `.lfxb` へ変換します。

```text
C:\FX\Source\Combat\Hit.lfxproj
    → C:\FX\Build\Combat\Hit.lfxb
```

ソース文書・ライブラリは書き換えません。出力が既にある場合は失敗し、
明示的な `--force` で上書きを許可します。
ファイル単位では一時ファイル経由で置換しますが、**一括処理全体がトランザクションではありません**。
途中で失敗しても、ほかの変換成功済みファイルが残ることがあります。

## テキスト一覧から変換する

```powershell
.\LiberaFxConverter.exe batch --list "C:\FX\effects.txt" "C:\FX\Build"
```

一覧には1行に1つの`.lfxproj`を書きます。相対パスは一覧ファイルの場所が基準です。
空行と`#`から始まるコメントは無視します。

```text
# effects.txt
Source/Radial.lfxproj
Source/Fountain.lfxproj
Source/Curl.lfxproj
```

既定では入力のファイル名を`.lfxb`へ変えて出力します。出力名を指定する場合は、
入力パスの後にTABと相対出力パス（例：`Particles/Fountain.lfxb`）を追加します。
`batch-list <一覧> <出力フォルダー>`でも同じ動作になります。
出力先の重複やフォルダー外へのパスは変換開始前に拒否します。
個別ファイルの失敗後も残りを処理し、1件でも失敗すれば終了コードは0以外になります。
既存出力の上書きには`--force`が必要です。

## 共有 Group とプロジェクト固有 Node

無人ビルドはエディターで選択中のプロジェクト設定を暗黙には参照しません。
必要なライブラリを明示します。

```powershell
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\FX\Build" `
    --groups "C:\FX\Libraries\Shared" `
    --groups "C:\FX\Libraries\Project" `
    --nodes "C:\FX\NodeDefinitions" `
    --force
```

| オプション | 動作 |
| --- | --- |
| `--groups <directory>` | 複数指定可。参照 Group を指定ライブラリの最新版へ解決します。不足・競合は保存済みコピーへ黙って戻さず失敗します。 |
| `--embedded-groups` | 保存された埋め込みリビジョンへ意図的に固定します。再現ビルド用の選択であり、エラー時の自動代替ではありません。 |
| `--nodes <directory>` | 複数指定可。プロジェクト固有の JSON 定義を読み込み、不正定義は拒否します。未実装の native Node やエンジン登録コードを作る機能ではありません。 |
| `--force` | 既存出力の上書き許可。古い不要ファイルの削除・差分ビルドをする機能ではありません。 |

再現性が必要なビルドでは、コンバーター・SDK・プロジェクト・Group／Node ライブラリの
リビジョンを固定してください。「最新版」は**その変換時に渡したライブラリ内の最新版**です。
リリースでは入力をスナップショット化し、デスクトップで開いている設定に依存させません。

## ゲームビルドへ組み込む

1. 固定した編集元を取得し、ソースとは別の新しい staging フォルダーを用意します。
2. 必要な Group／Node オプションを渡して `batch` を実行します。
3. **終了コードが0以外ならビルドを止めます。** 部分成功した staging や前回の古い成果物を配布しません。
4. 成功したパッケージをエンジンへ配置し、通常のインポート／再インポートと資源検証後、cook／build／export します。
5. デスクトップエディターを起動せず、読込と実画面を確認します。

```powershell
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\BuildStaging\FX" `
    --groups "C:\FX\Libraries\Shared" --force
if ($LASTEXITCODE -ne 0) {
    throw "Effect conversion failed; do not publish these outputs."
}
```

終了コードは**0＝すべて成功、1＝変換・入出力失敗、2＝コマンド指定不正**です。
入力が0件の場合も失敗します。Unity AssetDatabase・Unreal commandlet・Godot importer を
代行する CLI ではないため、エンジン側のビルドツールは変換後に実行します。
新しい staging を使うことで、削除・改名済みエフェクトの古い出力が残る事故も防げます。

## Resource Identity の診断用マニフェスト

登録済み ID を通常 `.lfxb` に出して黙って落とすことはできません。
別経路の `.lfxrm` 出力は**単体の `convert` のみ**です。

```powershell
.\LiberaFxConverter.exe convert "C:\FX\Mapped.lfxproj" "C:\FX\Build\Mapped.lfxrm" `
    --binding-snapshot "C:\FX\Bindings\snapshot.json" `
    --target-engine Unity --target-project "11111111-2222-4333-8444-555555555555"
```

例の GUID は実際のエンジンプロジェクトの永続 ID に置き換えます。
必要な場合のみ `--resource-map <file>` を追加し、文書内の対応付けと一致させます。
snapshot はそのプロジェクトの資源・プロパティの厳密な照会結果を呼び出し側が明示するものです。
**ライブ確認ではなく**、名前から推測した対応表でもありません。

`.lfxrm` は対応付け・診断・コンパイル済みデータを含みますが、
通常のエンジン用エフェクトとしてインポートできません。
ID 対応付け済みの通常パッケージ出力を解決するものではなく、
`batch` にこの出力オプションを渡すこともできません。
[ID と snapshot の仕様](https://github.com/Libra-Works/LibraFX/blob/main/docs/resource-identity.txt)を参照してください。

## ソースから使う場合

リポジトリのルートからは、同じ変換を次のように実行できます。

```powershell
dotnet run --project Tools\Converter -- batch "C:\FX\Source" "C:\FX\Build" --embedded-groups
```

こちらはリポジトリ所定の .NET SDK とビルド環境が必要です。
配布版コンバーターでは GUI 起動や `dotnet run` は不要です。
