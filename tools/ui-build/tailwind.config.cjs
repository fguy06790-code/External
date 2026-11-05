const path = require('node:path');
const colors = require('tailwindcss/colors');
module.exports = {
  content: [path.join(__dirname, '../../src/ExternalExecutor.App/Assets/*.{html,js}')],
  theme: {
    extend: {
      colors: {
        zinc: colors.neutral,
        gray: colors.neutral,
        ryn: {
          DEFAULT: '#eeeeee', hover: '#cccccc',
          glow: 'rgba(238, 238, 238, 0.25)', dark: '#0f0f0f',
          card: '#161616', border: '#262626'
        }
      },
      ringColor: { DEFAULT: '#eeeeee' },
      fontFamily: {
        mono: ['JetBrains Mono', 'Consolas', 'Cascadia Code', 'monospace'],
        sans: ['Inter', 'Segoe UI', 'Arial', 'sans-serif']
      }
    }
  },
  plugins: []
};
