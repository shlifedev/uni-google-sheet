import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  devToolbar: { enabled: false },
  integrations: [
    starlight({
      title: 'HolySheet',
      locales: { root: { label: '한국어', lang: 'ko' } },
      customCss: ['./src/styles/theme.css'],
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
