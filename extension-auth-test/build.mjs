import { build } from 'esbuild';
import { mkdirSync, copyFileSync } from 'node:fs';
mkdirSync('dist', { recursive: true });
await build({ entryPoints: ['src/app.js'], outfile: 'dist/app.js', bundle: true, format: 'iife', platform: 'browser', target: ['es2020'] });
copyFileSync('src/index.html', 'dist/index.html');
console.log('Extension assets generated in dist/');
