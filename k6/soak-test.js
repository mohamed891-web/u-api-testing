import http from 'k6/http';
import { check, sleep } from 'k6';

export let options = {

    vus: 50,

    duration: '30m',

    thresholds: {

        http_req_duration: ['p(95)<1000'],

        http_req_failed: ['rate<0.05']
    }
};

export default function () {

    let response = http.get(
        'http://localhost:65084/api/products?categoryId=18&page=1&pageSize=50'
    );

    check(response, {
        'status is 200': (r) => r.status === 200,
    });

    sleep(1);
}