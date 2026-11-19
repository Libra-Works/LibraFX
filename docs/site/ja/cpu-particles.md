---
layout: bilingual
title: CPU パーティクルとエミッター
lang: ja
translation_url: /cpu-particles/
---

# CPU パーティクルとエミッター

> 対応する更新版エディター・コンバーター・native library・エンジンアダプターが必要です。
> 旧 **2026.11.8 SDK には含まれません**。
> [Preview 配布ページ](https://github.com/Libra-Works/LibraFX/releases)の更新版を組み合わせてください。
> ビルドや回帰テストの結果だけでは、エンジン実行・UI 操作性・描画結果の確認にはなりません。

## Sprite の粒子群を作る

**CPU パーティクル**から CPU Particle を追加して開き、**生成（Spawn）** または
**更新（Update）** の子グラフを開きます。GPU 専用言語ではなく通常の CPU グラフです。
UUID・ピン・値・履歴・コピー貼り付け・保存は通常の子グラフと同じ仕組みです。
階層の移動自体は Undo を増やしません。

初期 Spawn は速度 `(0,1,0)` を設定し、Update は **CPU ステージイベント → 粒子を移動**
につながっています。初期値は位置原点・白色・サイズ `(1,1)`・経過時間ゼロ・寿命2秒です。
Spawn の Signal setter で初期属性を変え、Update のイベントから移動や属性変更を
つなぎます。移動は半陰的 Euler の順序です。

```
速度 += 加速度 × stepDelta
位置 += 速度 × stepDelta
```

属性ノードはステージ開始時の値を読みます。setter ごとに暗黙の再読込はしません。
**粒子のステップ時間差**はシミュレーションの時間差（生成時はゼロ）を返し、
通常の Frame Delta はホストのゲームフレーム時間差を維持します。
Spawn は生成時の一度、Update は正のシミュレーションステップごとに一度
（生成時にも delta=0 で一度）実行します。寿命に達した粒子は、その Update 前に消えます。

同階層の **CPU Particle Sprite Draw** が全粒子をバッチ描画します。粒子ごとの
actor/component は作りません。テクスチャ（`builtin:white` は明示的な組込リソース）、
Alpha/Additive、正の描画倍率を指定します。位置・XY サイズは effect-local で、
描画倍率は双方に適用します。Unity はエミッターごとの動的結合 Mesh、
Unreal は InstancedStaticMeshComponent、Godot は MultiMeshInstance3D を使います。
粒子ごとの RGBA・XY サイズを保持しますが、半透明の粒子単位ソートはありません。

Unreal は `USAGE.txt` 記載のインスタンス RGBA 対応 Sprite マテリアルが必要です。
**そのマテリアル自体は今回同梱していません**。不足・非互換なら明示的に失敗し、
仮リソースへ置換しません。

## 放出・形状・決定的な乱数

コンテナで粒子上限・UInt32 Seed・既定の毎秒放出数・開始時バーストを指定します。
**Emitted total** を接続すると既定の連続放出率に代わって累積放出数を読みます
（開始時バーストは引き続き加算）。

* **連続放出エミッター**は毎秒放出数の端数を繰り越します。
* **バーストエミッター**は通常の Signal が届くたびに設定した固定数を加算します。
  連続放出と併用する場合は、両方の累積出力を Add で加算します。
* **Box 放出位置**は半径幅内、**Sphere 放出位置**は球の体積内、
  **Circle 放出位置**は XY 円周です。Spawn の位置 setter へ接続します。
  定位置の設定が Point 放出に相当します。

サンプリングはシステム Seed と粒子 ID を使い、グローバル RNG を使いません。
ID・Seed は通常の決定的 Random ノードの SampleIndex・Seed にも接続できます。
ID は再生中に単調増加する非ゼロ UInt32 で、スロット再利用時にも再利用しません。
枯渇はエラーです。同じグラフ・Seed・ステップ列での決定性であり、
任意のフレーム分割が同じ軌跡になる保証ではありません。

## 通常の Property・Signal のやり取り

Spawn・Update に名前付きの粒子 Float・Vector3・Vector4 入出力を追加すると、
コンテナにピンが現れます。その他の数値型は通常の明示的な CPU 変換を使います。
Matrix・Int32・Color・Vector2 専用の境界はまだありません。演算・比較・Signal capture・
対応済みの通常 custom node は GPU DSL に置換せず、粒子ごとの VM 状態で実行します。

入力 Property はシステムの各ステップで一度コピーし、そのステップの全粒子が
一貫した値を読みます。Spawn 入力は生成時の値、Update 入力はライブ値です。
**粒子 Signal 入力は Update に置きます**。外側からの各到着は有界 FIFO に入り、
次のステップで処理される全粒子（そのステップの新生粒子も含む）へ broadcast します。
Spawn は継続的な外部イベントのスコープではありません。

出力 Property・Signal は次の識別情報を保持したキューメッセージです。

```
systemId, nodeId, particleId, time, kind, width, values[4]
```

同じステップで粒子が消えても通知は残ります。外部コードは
`SpawnableHost::popCpuParticleEvent`、対応 C ABI、または各アダプターの
イベント読出 API で取得します。**利用側は有界イベントキューを読み出してください**。
満杯ならエラーになり、黙って破棄しません。simulation worker から
エンジン SDK の callback を呼びません。

多数の粒子を一つの外側 Property に接続する場合、コンテナで
`outputReduction = LastById` を明示しなければコンパイルを拒否します。
これはスロット評価順ではなく最大の粒子 ID を選びます。集約値と外向き Signal は
次のシミュレーションステップに配送します。個々の値・ID が必要な場合は
scoped event API を使ってください。フィードバックは再帰的実行ではなくキューです。

## 制限・所有権・互換性

* root と通常 Graph Group 内の CPU システムが対象です。Spawnable Group 内、
  CPU/GPU コンテナのステージ内入れ子、ステージ内 Draw、
  `Delay`・`LifeTime` 等の遅延 lifecycle は明示的にコンパイルを拒否します。
* Sprite billboard が対象です。Mesh Draw・Cone・衝突・trail・粒子 ID 指定の
  外部 Signal 配送は未実装です。グラフ入力 Signal は broadcast です。
* 外向き Signal は、Reset などの名前付き入力も含め、配線した入力に届きます。
  コンパイラが出力プログラムに中継を追加するため、グラフに中継ノードを置く必要はありません。
* 粒子上限とキュー上限は固定です。放出要求超過・容量不足・メモリ上限・キュー満杯は
  エラーになり、既に実行した部分はロールバックしません。設定を修正して再生し直します。
* whole-effect host が粒子スロットと連続 work/default バッファを所有し、
  CPU 粒子更新は初期確保後に粒子・ノード・フレームごとのヒープ確保をしません。
  無関係な既存 Spawnable 処理全体が allocation-free という主張ではありません。
* 時刻は単調増加です。逆方向 seek は再生を作り直します。自然終了は放出停止後に
  生存粒子を排出し、Stop・Force は粒子と CPU 通知を消去します。
* CPU を含む `.lfxb` は既存 `LFXP` envelope の **format 4 / package 12** と、
  little-endian の CPU payload v1 を使います。非 CPU は既存 format 3 / package 11 を維持します。
  更新版 reader は旧形式を読み、旧 reader は CPU を捨てず非対応として拒否します。
* 描画 metadata に `cpuParticleVersion:1` と `cpuParticles` を追加します。
  native snapshot は80バイト、scoped event は88バイトです。既存 UUID・値 ABI は変えません。

standalone converter はエディター起動なしで CPU を出力します。既存の GPU 拒否は維持します。
グラフ・package・native pool の回帰テストと、コンパイラーで生成したバイナリーの
native 実行を検証対象にしています。エンジン上の描画確認は別途必要です。
