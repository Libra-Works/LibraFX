# LIBERA FX ドキュメント

現在は、非公開リポジトリ内のMarkdownで管理します。GitHub Pagesは有効化せず、
一般公開と自動デプロイは、公開範囲を確認して明示的に承認する段階まで保留します。
このディレクトリの追加だけで公開を開始することはありません。

## 既存資料

- [入門](getting-started.md)
- [寿命の考え方](lifetime.md)
- [小さな実例](small-example.md)
- [文書用グラフ撮影と更新](documentation-capture.md)
- [SDKの導入と配布準備](sdk-distribution.md)
- [GPUバックエンドとCore Runtimeの契約](gpu-backends.md)

- [イントロダクション](../イントロダクション.md)
- [概要書](../概要書.md)
- [Unity連携](../Runtime/Integrations/Unity/USAGE.txt)
- [Unreal連携](../Runtime/Integrations/Unreal/USAGE.txt)
- [ランタイムのバイトコード仕様](../Runtime/BYTECODE.md)
- [SignalタイムラインとプレビューSend](signal-timeline.md)
- [外部定義の型変換](type-conversions.md)
- [資源ワークスペース](resource-workspace.md)
- [使用中アセットと参照ビュー](asset-references.md)
- [Transformの演算順序](transform-order.md)
- [World LookAtとBillboard](lookat-billboards.md)
- [標準Groupモジュール](standard-modules.md)
- [ポータブル数値演算の実測](../Runtime/MATH-BACKEND-MEASUREMENTS.md)
- [実行時Enum分岐](runtime-enum-branches.md)
- [数値比較・論理Signal・Signalから数値への変換](logical-signals.md)

これらはリポジトリ内で読むためのリンクです。既存の構想・仕様と実装済みの範囲を
整理した最初の三ページと、同梱サンプルに限定した撮影APIを用意しています。
実画面はこの文書だけでは生成されず、文書・画像の自動更新とPagesへの公開も未実装です。

## 更新方針

- 機能を追加・変更した際は、影響する説明・操作手順・制限事項も同じ変更で更新します。
- 実装済み、確認済み、未対応、未検証を区別し、構想を利用可能な機能として説明しません。
- 画像は承認済みの専用サンプルから取得します。実ユーザーのプロジェクト、個人情報、
  認証情報、ローカルログを文書へ含めません。
- 撮影時の言語・画面サイズ・テーマ・表示位置を揃え、同じ状態を再現できるようにします。
  エンジン上の再生結果は、グラフの画像とは区別します。
- AIやMCPによる更新も差分を確認し、説明と実際の動作、画像参照、リンクを照合します。

## 将来の公開

GitHub Pagesは、元のリポジトリが非公開でもサイト自体が一般公開される場合があります。
リポジトリの閲覧権限がそのままサイトに適用されるとは扱いません。

公開時には配信する文書・画像を選び直し、内部資料やソース一式を公開対象へ含めないこと、
公開用リンクが成立することを確認します。本体リポジトリを公開へ切り替えることや、
Pagesを有効化することは、別途の承認なしには行いません。
