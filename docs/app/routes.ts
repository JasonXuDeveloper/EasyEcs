import { index, type RouteConfig, route } from '@react-router/dev/routes';

export default [
  index('routes/redirect-root.tsx'),
  // Prerendered and copied to build/client/404.html
  route('404', 'routes/not-found.tsx', { id: 'routes/404' }),
  route(':lang', 'routes/home.tsx'),
  route(':lang/docs/*', 'routes/docs.tsx'),
  route('api/search.json', 'routes/search.ts'),
  route('*', 'routes/not-found.tsx'),
] satisfies RouteConfig;
