---
title: スカラー数学ノード
---

# スカラー数学ノード

時間や距離などの数値を、エフェクトの調整に使いやすい値へ変換する Float ノードです。0〜1 を繰り返す位相は **Fraction**、範囲内の値から補間量を作るなら **Inverse Lerp → Saturate → Lerp** を使います。

ここで説明するのは CPU のパラメーターノードです。GPU パーティクルの計算を編集するときは GPU カーネルのパレットを使います。

| ノード | 結果 |
|---|---|
| Modulo（剰余） | `fmod(Value, Divisor)`。結果の符号は被除数と同じです。除数がゼロなら失敗します。 |
| Fraction（小数部分） | `Value - floor(Value)`。`-1.25` は `0.75` になります。 |
| Ceil（切り上げ） | 正の無限大方向の整数。`-1.25` は `-1` です。 |
| Truncate（ゼロ方向に切り捨て） | ゼロ方向の整数。`-1.25` は `-1` です。 |
| Exp / Exp2 | `e^Value` / `2^Value`。 |
| Smoothstep | `t = clamp((Value - Low) / (High - Low), 0, 1)` として `t*t*(3-2*t)`。Low は High より小さくする必要があります。 |
| Inverse Lerp（逆線形補間） | `(Value - Start) / (End - Start)`。制限なし。同じ端点は失敗し、逆順の端点は使用できます。 |
| Saturate | 値を `[0, 1]` に制限します。 |
| Step | Edge 未満なら `0`、Edge 以上なら `1`。 |

Inverse Lerp の後に Lerp をつなぐと、入力範囲を出力範囲に変換できます。
範囲を制限したい場合はその間に Saturate を入れます。入力はすべて有限値に
してください。オーバーフローなどの非有限の計算は `NonFinite`、
Modulo と Inverse Lerp のゼロ除算は `DivideByZero` です。Smoothstep の
等しい／逆順の境界は `InvalidArgument` です。アンダーフローはゼロになり得ます。

## 次に進む

- [エンジンに接続してエフェクトを再生する](production-workflow.md)。
- [表示されない・思った動きにならないときの確認](troubleshooting.md)。
- [標準ノードの働きを調べる](node-reference.md)。
