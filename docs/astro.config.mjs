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
        { label: '시작하기', items: [{ slug: '' }] },
        { label: '사용 가이드', items: [{ autogenerate: { directory: 'guides' } }] },
      ],
    }),
  ],
});
