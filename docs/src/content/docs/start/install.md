---
title: 설치하기
description: Unity 프로젝트에 HolySheet 패키지를 추가하고 설정 파일을 만듭니다.
sidebar:
  order: 1
---

Unity 프로젝트에 패키지를 추가하고, 직렬화 라이브러리를 넣고, 설정 파일을 만드는 세 단계입니다. Unity 없이 쓰려면 [데스크톱 앱](../../tools/desktop/)이나 [명령줄 도구](../../tools/cli/)를 보세요.

## 준비물

- Unity 6000.0 이상
- 직렬화 라이브러리 하나: MemoryPack(기본값) 또는 MessagePack

## 1. 패키지 추가하기

1. Unity에서 **Window → Package Management → Package Manager**를 엽니다.
2. **+ → Install package from git URL**을 선택하고 아래 주소를 입력합니다.

   ```text
   https://github.com/shlifedev/uni-google-sheet.git?path=src#dev/feat-2026-remaster
   ```

   주소 끝의 `#dev/feat-2026-remaster`는 버전 2를 개발하는 브랜치를 가리킵니다.

## 2. 직렬화 라이브러리 추가하기

생성되는 코드는 직렬화 라이브러리를 사용합니다. 기본 설정은 MemoryPack입니다.

- [MemoryPack의 Unity 설치 안내](https://github.com/Cysharp/MemoryPack#unity)를 따라 프로젝트에 추가하고, 스크립트 컴파일이 끝날 때까지 기다립니다.
- MessagePack을 쓰려면 [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp)의 Unity 안내를 따라 추가하고, 나중에 **설정 → 출력 → 직렬화**를 `messagepack`으로 바꿉니다.

라이브러리가 없으면 HolySheet 창이 경고를 표시하고, 생성된 코드가 컴파일되지 않습니다.

| | MemoryPack | MessagePack |
| --- | --- | --- |
| 특징 | 불러오는 속도가 가장 빠릅니다. | 파일이 작고 다른 언어에서도 읽을 수 있습니다. |
| 고르는 기준 | 데이터를 읽는 쪽이 모두 C#일 때 | C#이 아닌 서버나 도구도 데이터를 읽을 때 |

## 3. 설정 파일 만들기

1. **Tools → HolySheet → Tables**로 HolySheet 창을 엽니다.
2. **holysheet.json 만들기**를 누릅니다.

프로젝트 폴더에 다음 두 가지가 생깁니다.

```text
MyGame/
├─ Assets/
├─ Sheets/            ← 여기에 .xlsx 파일을 넣습니다
└─ holysheet.json     ← 시트 위치와 출력 폴더를 담은 설정 파일
```

`holysheet.json`은 HolySheet 창의 **설정** 페이지에서 고칩니다. 파일을 직접 고치려면 [holysheet.json 레퍼런스](../../reference/config/)를 보세요.

## 버전 관리

| 대상 | 저장소에 올리나요 |
| --- | --- |
| `holysheet.json`, `Sheets/` | 올립니다. 팀원과 CI가 같은 설정으로 빌드합니다. |
| `.holysheet/` | 올리지 않습니다. 빌드 캐시입니다. 첫 빌드 때 캐시 폴더 안에 `.gitignore`가 생겨 Git에서 제외됩니다. |
| 서비스 계정 키, `holysheet.private.key` | 올리지 않습니다. `.gitignore`에 추가하세요. |

## 데모 살펴보기

완성된 예제가 필요하면 Package Manager에서 HolySheet 패키지를 선택하고 **Samples → Demo → Import**를 누르세요. `Demo.unity` 씬을 열고 Play를 누르면 RPG 예제 데이터를 불러와 보여 주고, 다시 불러오기와 변조 감지를 직접 시험할 수 있습니다. 데모는 MemoryPack이 필요합니다.

## 다음 단계

[첫 테이블 만들기](../first-table/)에서 아이템 하나를 입력하고 게임에서 읽어 봅니다.
