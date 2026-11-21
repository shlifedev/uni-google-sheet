# HolySheet 사용자 문서

본문은 `src/content/docs/`의 Markdown 파일입니다. 첫 페이지는 [첫 아이템 테이블 만들기](src/content/docs/index.md)에서 수정하세요. [사용 가이드](src/content/docs/guides/)에 `.md` 파일을 추가하면 사이드바에 표시됩니다. 파일 상단의 `title`로 제목을, `sidebar.order`로 순서를 지정합니다.

Node.js 22.23.4 이상을 설치한 뒤 저장소 루트에서 실행하세요.

```sh
cd docs
npm ci
npm run dev
```

터미널에 표시된 로컬 주소를 열면 수정한 내용을 바로 확인할 수 있습니다. 검색까지 확인하려면 `npm run build` 후 `npm run preview`를 실행하세요.
