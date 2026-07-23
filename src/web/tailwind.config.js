/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // DMARC Analyzer design system — deep ink-green + teal/mint.
        teal: {
          50: '#effaf7', 100: '#d7f4ec', 200: '#afe9db', 300: '#7eddc7', 400: '#45c7aa',
          500: '#16ad8d', 600: '#0e9481', 700: '#0c7568', 800: '#0b5d54', 900: '#0a4a44', 950: '#062f2b',
        },
        mint: { 300: '#5ff0c0', 400: '#3ae0b0', 500: '#22c996' },
        ink: { 900: '#0b1d18', 800: '#0e2620', 700: '#123029' },
        gray: {
          25: '#fafcfb', 50: '#f5f8f7', 100: '#eef3f1', 200: '#e3eae8', 300: '#cfdad6',
          400: '#9fb0ab', 500: '#6b807a', 600: '#54685f', 700: '#3d4f49', 800: '#25332f', 900: '#101f1c',
        },
        amber: { 100: '#fdf0d5', 500: '#d97706', 600: '#b45309', 800: '#8a4406' },
        red: { 100: '#fde5ea', 600: '#dc3d5c', 800: '#a81f3d' },
        blue: { 100: '#dbeafe', 600: '#2563ab' },

        // Semantic aliases (map to CSS vars for a single source of truth).
        brand: { DEFAULT: 'var(--brand)', hover: 'var(--brand-hover)', active: 'var(--brand-active)', subtle: 'var(--brand-subtle)' },
        border: { DEFAULT: 'var(--border-default)', strong: 'var(--border-strong)' },
        surface: { page: 'var(--surface-page)', card: 'var(--surface-card)', sunken: 'var(--surface-sunken)', ink: 'var(--surface-ink)' },
        body: 'var(--text-body)',
        secondary: 'var(--text-secondary)',
        faint: 'var(--text-faint)',
        link: 'var(--link)',
      },
      fontFamily: {
        display: ['"Space Grotesk"', 'ui-sans-serif', 'system-ui', 'sans-serif'],
        body: ['"Public Sans"', 'ui-sans-serif', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'SF Mono', 'Menlo', 'monospace'],
      },
      fontSize: {
        xs: '12px', sm: '13px', base: '14px', md: '15px', lg: '18px',
        xl: '22px', '2xl': '28px', '3xl': '36px', '4xl': '48px', '5xl': '60px',
      },
      letterSpacing: { tightest: '-0.03em', tight: '-0.02em', wide: '0.06em' },
      borderRadius: { xs: '6px', sm: '8px', md: '10px', lg: '14px', xl: '18px', pill: '999px' },
      boxShadow: {
        card: '0 1px 2px rgba(11,29,24,.05)',
        raised: '0 4px 12px rgba(11,29,24,.08),0 1px 2px rgba(11,29,24,.05)',
        overlay: '0 20px 48px rgba(11,29,24,.18),0 4px 12px rgba(11,29,24,.08)',
        'ink-panel': '0 24px 60px rgba(6,47,43,.25)',
      },
      transitionTimingFunction: { out: 'cubic-bezier(.2,.8,.3,1)' },
    },
  },
  plugins: [],
}
