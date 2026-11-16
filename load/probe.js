// Sends one request shape at a fixed rate, to measure a single hop in isolation.
// TARGET picks the hop; RATE is requests per second. See load/README.md.

import http from 'k6/http';

const HOST = __ENV.HOST || 'http://host.docker.internal';
const RATE = Number(__ENV.RATE || 100);
const product = (n) => `a0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const ids = Array.from({ length: 20 }, (_, i) => `ids=${product(i + 1)}`).join('&');
const json = { headers: { 'Content-Type': 'application/json' } };

const targets = {
  'catalog-search': () => http.get(`${HOST}:5101/products/search?q=standing%20desk&limit=5`),
  'catalog-page': () => http.get(`${HOST}:5101/products?pageSize=20`),
  'inventory-batch': () => http.get(`${HOST}:5102/stock/batch?${ids}`),
  'gateway-search': () => http.post(`${HOST}:5100/graphql`, JSON.stringify({
    query: '{ searchProducts(query: "standing desk", first: 5) { id name priceCents availableStock } }',
  }), json),
  'gateway-page': () => http.post(`${HOST}:5100/graphql`, JSON.stringify({
    query: '{ products(pageSize: 20) { total items { id name priceCents availableStock } } }',
  }), json),
  'gateway-health': () => http.get(`${HOST}:5100/health/live`),
};

export const options = {
  scenarios: {
    probe: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: __ENV.DURATION || '20s',
      preAllocatedVUs: 50,
      maxVUs: 1000,
    },
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max', 'count'],
};

export default targets[__ENV.TARGET];
