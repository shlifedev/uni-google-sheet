---
title: 시트 나누기와 설정 테이블
description: 큰 테이블을 여러 시트에 나눠 적는 방법과, 값이 한 벌뿐인 설정 테이블을 만드는 방법입니다.
sidebar:
  order: 5
---

테이블이 커지거나 여러 사람이 함께 고치면 시트 한 장으로는 불편해집니다. 탭 이름에 점(`.`)을 붙여 한 테이블을 여러 시트에 나눠 적을 수 있습니다. 게임 코드에서는 여전히 테이블 하나입니다.

| 탭 이름 | 하는 일 |
| --- | --- |
| `Item.Weapons` | `Item` 테이블에 **행**을 더합니다. |
| `Item.Col.Balance` | `Item` 테이블에 **열**을 더합니다. |
| `Game.Config` | 값이 한 벌뿐인 **설정 테이블** `Game`을 만듭니다. |

점 뒤의 이름(`Weapons`, `Balance`)은 자유롭게 정합니다. 나눈 시트는 다른 워크북에 있어도 됩니다.

## 행을 나눠 적기

무기는 `Item.Weapons`, 방어구는 `Item.Armors`처럼 종류별로 시트를 나누는 방법입니다. 기준이 되는 `Item` 시트는 반드시 있어야 하고, 나머지 시트의 행이 그 뒤에 이어 붙습니다.

**Item**

| `Key<int>` | `string` | `int` |
| --- | --- | --- |
| Id | Name | Price |
| 아이템 번호 | 이름 | 가격 |
| 2001 | 체력 물약 | 25 |

**Item.Weapons**

| `Key<int>` | `string` | `int` |
| --- | --- | --- |
| Id | Name | Price |
| 아이템 번호 | 이름 | 가격 |
| 1001 | 나무 검 | 100 |
| 1003 | 철 검 | 450 |

빌드하면 `Item` 테이블에 세 행이 들어갑니다.

- 나눈 시트는 `Item` 시트와 **같은 이름, 같은 타입의 열**을 모두 가져야 합니다. 열의 순서는 달라도 됩니다.
- 키는 테이블 전체에서 겹치지 않아야 합니다.
- 나눈 시트가 여러 장이면 시트 이름의 순서대로 이어 붙습니다.

## 열을 나눠 적기

기획자는 이름과 설명을, 밸런스 담당자는 수치를 고치는 식으로 역할이 나뉠 때 씁니다. 열을 더하는 시트에는 **키 열**과 **추가할 열**만 적습니다.

**Item.Col.Balance**

| `Key<int>` | `int` | `int` |
| --- | --- | --- |
| Id | Attack | Defense |
| 아이템 번호 | 공격력 | 방어력 |
| 1001 | 12 | 0 |
| 1003 | 30 | 0 |

빌드하면 `Item` 테이블에 `Attack`, `Defense` 필드가 생깁니다.

- 행은 첫 번째 키 열의 값으로 연결합니다. 행의 순서는 `Item` 시트와 달라도 됩니다.
- 여기에 적지 않은 행(위 예의 `2001`)은 빈 셀로 처리되어 `0` 같은 기본값이 됩니다.
- `Item`에 없는 키를 적으면 검사에서 오류가 납니다.

## 설정 테이블

최대 레벨, 골드 배율처럼 게임 전체에 값이 하나씩만 있는 데이터는 탭 이름을 `이름.Config`로 하고 **세로로** 적습니다. 헤더 행 없이 한 줄이 필드 하나입니다.

<div class="hs-sheet not-content">
<table>
<thead><tr><th></th><th>A</th><th>B</th><th>C</th><th>D</th></tr></thead>
<tbody>
<tr><th>1</th><td>int</td><td>MaxLevel</td><td>캐릭터 최대 레벨</td><td>60</td></tr>
<tr><th>2</th><td>float</td><td>GoldRate</td><td>골드 획득 배율</td><td>1.5</td></tr>
<tr><th>3</th><td>Ref&lt;Item&gt;</td><td>StartItem</td><td>처음에 지급하는 아이템</td><td>1001</td></tr>
<tr class="hs-legend"><th></th><td>타입</td><td>필드 이름</td><td>설명</td><td>값</td></tr>
</tbody>
</table>
<div class="hs-tabs"><span class="hs-on">Game.Config</span></div>
</div>

코드에서는 `Value`로 바로 읽습니다.

```csharp
int maxLevel  = tables.Game.Value.MaxLevel;
float rate    = tables.Game.Value.GoldRate;
ItemRow start = tables.Item.Get(tables.Game.Value.StartItem);
```

## 워크북 나누기

시트를 워크북 여러 개에 나눠 담아도 결과는 같습니다. `Sheets` 폴더에 `Item.xlsx`, `Monster.xlsx`, `Stage.xlsx`를 두면 모두 읽어서 하나의 테이블 묶음으로 만듭니다. Excel, CSV, Google Sheets를 섞어 써도 됩니다.

지켜야 할 규칙은 하나입니다. **테이블 이름은 프로젝트 전체에서 겹치지 않아야 합니다.** 두 워크북에 `Item` 시트가 각각 있으면 검사에서 오류가 납니다. 한 테이블을 두 워크북에 나누려면 한쪽 탭 이름을 `Item.Weapons`처럼 바꾸세요.
