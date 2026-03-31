/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        border: 'hsl(210 30% 86%)',
        input: 'hsl(210 30% 86%)',
        ring: 'hsl(172 84% 32%)',
        background: 'hsl(207 58% 97%)',
        foreground: 'hsl(220 26% 14%)',
        primary: {
          DEFAULT: 'hsl(172 84% 32%)',
          foreground: 'hsl(0 0% 100%)',
        },
        secondary: {
          DEFAULT: 'hsl(205 81% 92%)',
          foreground: 'hsl(215 32% 21%)',
        },
        muted: {
          DEFAULT: 'hsl(207 48% 94%)',
          foreground: 'hsl(216 23% 42%)',
        },
        accent: {
          DEFAULT: 'hsl(193 92% 89%)',
          foreground: 'hsl(213 34% 24%)',
        },
        destructive: {
          DEFAULT: 'hsl(0 73% 54%)',
          foreground: 'hsl(0 0% 100%)',
        },
        card: {
          DEFAULT: 'hsl(0 0% 100%)',
          foreground: 'hsl(220 26% 14%)',
        },
      },
      borderRadius: {
        lg: '0.9rem',
        md: '0.65rem',
        sm: '0.5rem',
      },
      boxShadow: {
        panel: '0 14px 34px rgba(16, 45, 73, 0.12)',
      },
    },
  },
  plugins: [],
}
