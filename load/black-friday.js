// "Black Friday" for Plaza: people browse, some buy, and at one moment far more people want a
// product than there are units of it. Run with k6 (see load/README.md).
//
// Load is generated at a fixed arrival rate (an open model): requests keep arriving whether
// or not the system keeps up, the way real customers do. A closed model, where each virtual
// user waits for its answer before sending the next request, would slow down with the system
// and hide exactly the queueing this test is looking for.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const GATEWAY = __ENV.GATEWAY_URL || 'http://host.docker.internal:5100';
const INVENTORY = __ENV.INVENTORY_URL || 'http://host.docker.internal:5102';

// Peak arrival rates, per second. The defaults are the "Black Friday" profile; SCALE shrinks
// or grows all of them together.
const SCALE = Number(__ENV.SCALE || 1);
const BROWSE_PEAK = Math.round(Number(__ENV.BROWSE_PEAK || 120) * SCALE);
const CHECKOUT_PEAK = Math.round(Number(__ENV.CHECKOUT_PEAK || 30) * SCALE);
const FLASH_BUYERS = Number(__ENV.FLASH_BUYERS || 300);
const FLASH_STOCK = Number(__ENV.FLASH_STOCK || 100);

const FINAL = ['Confirmed', 'Rejected', 'PaymentFailed', 'Expired'];
const product = (n) => `a0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const FLASH_PRODUCT = product(20);
const REGULAR_PRODUCTS = Array.from({ length: 19 }, (_, i) => product(i + 1));
const SEARCHES = ['standing desk', 'ergonomic chair', 'monitor', 'mechanical keybord', 'wireless mouse',
  'webcam', 'desk lamp', 'usb-c dock', 'laptop stand', 'headset noise cancelling'];

// Time from placing an order until it is Confirmed, Rejected, PaymentFailed or Expired.
const checkoutTime = new Trend('checkout_time_to_final', true);
const ordersPlaced = new Counter('orders_placed');
const ordersConfirmed = new Counter('orders_confirmed');
const ordersNotFinal = new Counter('orders_not_final_in_time');
const flashConfirmed = new Counter('flash_orders_confirmed');
const flashRejected = new Counter('flash_orders_rejected');
const flashOther = new Counter('flash_orders_other');
const graphqlErrors = new Rate('graphql_errors');

export const options = {
  discardResponseBodies: false,
  scenarios: {
    // Anonymous visitors searching and paging through the catalogue with stock.
    browse: {
      executor: 'ramping-arrival-rate',
      exec: 'browse',
      startRate: Math.max(1, Math.round(BROWSE_PEAK / 10)),
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: 2000,
      stages: [
        { duration: '20s', target: Math.max(1, Math.round(BROWSE_PEAK / 4)) },
        { duration: '40s', target: BROWSE_PEAK },
        { duration: '60s', target: BROWSE_PEAK },
        { duration: '10s', target: 0 },
      ],
    },
    // Customers placing an order and waiting for it to finish.
    checkout: {
      executor: 'ramping-arrival-rate',
      exec: 'checkout',
      startRate: Math.max(1, Math.round(CHECKOUT_PEAK / 10)),
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: 3000,
      stages: [
        { duration: '20s', target: Math.max(1, Math.round(CHECKOUT_PEAK / 4)) },
        { duration: '40s', target: CHECKOUT_PEAK },
        { duration: '60s', target: CHECKOUT_PEAK },
        { duration: '10s', target: 0 },
      ],
    },
    // The doorbuster: everyone hits "buy" on the same scarce product at the same moment,
    // while the rest of the traffic is at its peak.
    flash_sale: {
      executor: 'per-vu-iterations',
      exec: 'flashSale',
      vus: FLASH_BUYERS,
      iterations: 1,
      startTime: '70s',
      maxDuration: '90s',
    },
  },
  // The service levels this system is expected to keep at the Black Friday profile.
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'graphql_errors': ['rate<0.01'],
    'http_req_duration{scenario:browse}': ['p(95)<300'],
    // Ordinary checkouts, and separately the queue of buyers for the one scarce product.
    'checkout_time_to_final{scenario:checkout}': ['p(95)<5000'],
    'checkout_time_to_final{scenario:flash_sale}': ['p(95)<30000'],
    'orders_not_final_in_time': ['count==0'],
    // Correctness under load: exactly the units that exist are sold, never one more.
    'flash_orders_confirmed': [`count==${Math.min(FLASH_STOCK, FLASH_BUYERS)}`],
    'flash_orders_other': ['count==0'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
};

export function setup() {
  const json = { headers: { 'Content-Type': 'application/json' } };

  // Plenty of everything, except the doorbuster.
  for (const id of REGULAR_PRODUCTS) {
    http.put(`${INVENTORY}/stock/${id}`, JSON.stringify({ available: 1000000 }), json);
  }
  const flash = http.put(`${INVENTORY}/stock/${FLASH_PRODUCT}`, JSON.stringify({ available: FLASH_STOCK }), json);
  if (flash.status !== 200) {
    throw new Error(`Could not set stock: ${flash.status} ${flash.body}`);
  }

  // One customer per token; virtual users share them round-robin.
  const tokens = [];
  for (let i = 0; i < 500; i++) {
    tokens.push(http.post(`${GATEWAY}/dev/token`).json('accessToken'));
  }

  // Warm up. A system that has just started pays one-off costs (JIT compilation, connection
  // pools, Kafka consumer groups joining) that say nothing about its capacity. They are paid
  // here, before the clock starts.
  for (let round = 0; round < 3; round++) {
    for (let i = 0; i < 20; i++) {
      const term = SEARCHES[i % SEARCHES.length];
      graphql('query($q: String!) { searchProducts(query: $q, first: 5) { id availableStock } }', { q: term }, null, 'warmup');
      graphql('{ products(pageSize: 20) { items { id availableStock } } }', null, null, 'warmup');
    }
    for (let i = 0; i < 10; i++) {
      placeAndFollow(tokens[i], REGULAR_PRODUCTS[i], 1, 60);
    }
  }

  return { tokens };
}

function graphql(query, variables, token, name) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }
  const response = http.post(`${GATEWAY}/graphql`, JSON.stringify({ query, variables }), { headers, tags: { name } });
  let body = null;
  try {
    body = response.json();
  } catch (_) {
    // Not JSON: an error page or an empty body. Counted below.
  }
  const ok = response.status === 200 && body !== null && !body.errors;
  graphqlErrors.add(!ok);
  if (!ok && __ENV.LOG_ERRORS && Math.random() < 0.01) {
    console.error(`[${name}] ${response.status} ${String(response.body).substring(0, 300)}`);
  }
  return ok ? body.data : null;
}

export function browse() {
  if (Math.random() < 0.6) {
    const term = SEARCHES[Math.floor(Math.random() * SEARCHES.length)];
    const data = graphql(
      'query($q: String!) { searchProducts(query: $q, first: 5) { id name priceCents availableStock } }',
      { q: term }, null, 'search');
    check(data, { 'search returned products': (d) => d !== null && d.searchProducts.length > 0 });
  } else {
    const data = graphql('{ products(pageSize: 20) { total items { id name priceCents availableStock } } }', null, null, 'catalogue');
    check(data, { 'catalogue page returned': (d) => d !== null && d.products.items.length === 20 });
  }
}

function placeAndFollow(token, productId, quantity, maxSeconds) {
  const started = Date.now();
  const placed = graphql(
    'mutation($input: PlaceOrderInput!) { placeOrder(input: $input) { order { id status } errors } }',
    { input: { paymentToken: 'tok_test_visa', lines: [{ productId, quantity }] } }, token, 'placeOrder');

  if (placed === null || placed.placeOrder.order === null) {
    return null;
  }
  ordersPlaced.add(1);

  const id = placed.placeOrder.order.id;
  let status = placed.placeOrder.order.status;
  while (!FINAL.includes(status) && Date.now() - started < maxSeconds * 1000) {
    sleep(0.25);
    const data = graphql('query($id: UUID!) { order(id: $id) { status } }', { id }, token, 'order');
    if (data !== null && data.order !== null) {
      status = data.order.status;
    }
  }

  if (!FINAL.includes(status)) {
    ordersNotFinal.add(1);
    return 'NotFinal';
  }
  checkoutTime.add(Date.now() - started);
  return status;
}

export function checkout(data) {
  const token = data.tokens[Math.floor(Math.random() * data.tokens.length)];
  const productId = REGULAR_PRODUCTS[Math.floor(Math.random() * REGULAR_PRODUCTS.length)];
  const status = placeAndFollow(token, productId, 1 + Math.floor(Math.random() * 2), 60);
  if (status === 'Confirmed') {
    ordersConfirmed.add(1);
  }
  check(status, { 'order confirmed': (s) => s === 'Confirmed' });
}

export function flashSale(data) {
  const token = data.tokens[(__VU - 1) % data.tokens.length];
  const status = placeAndFollow(token, FLASH_PRODUCT, 1, 80);
  if (status === 'Confirmed') {
    flashConfirmed.add(1);
  } else if (status === 'Rejected') {
    flashRejected.add(1);
  } else {
    flashOther.add(1);
  }
}

export function teardown() {
  // The last word on overselling belongs to Inventory, not to what the clients saw.
  const stock = http.get(`${INVENTORY}/stock/${FLASH_PRODUCT}`).json();
  console.log(`flash product stock after the sale: available=${stock.available} reserved=${stock.reserved}`);
  if (stock.available < 0 || stock.reserved !== 0) {
    throw new Error(`Stock is inconsistent after the sale: ${JSON.stringify(stock)}`);
  }
}
