import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  devToolbar: { enabled: false },
  integrations: [
    starlight({
      title: 'HolySheet',
      locales: { root: { label: '한국어', lang: 'ko' } },
      customCss: ['./src/styles/theme.css'],
      // 각 폴더에 .md 파일을 추가하면 그 묶음에 나타납니다. 순서는 파일의 sidebar.order로 정합니다.
      sidebar: [
        { label: '시작하기', items: [{ slug: '' }, { autogenerate: { directory: 'start' } }] },
        { label: '시트 작성하기', items: [{ autogenerate: { directory: 'sheets' } }] },
        { label: '도구 사용하기', items: [{ autogenerate: { directory: 'tools' } }] },
        { label: '게임에서 사용하기', items: [{ autogenerate: { directory: 'runtime' } }] },
        { label: '참고', items: [{ autogenerate: { directory: 'reference' } }] },
      ],
    }),
  ],
});
