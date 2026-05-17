import http from 'k6/http';
import { check, sleep } from 'k6';

export let options = {

    stages: [

        // WARMUP
        { duration: '20s', target: 100 },

        // HEAVY LOAD
        { duration: '30s', target: 500 },

        // EXTREME LOAD
        { duration: '30s', target: 1000 },

        // COOL DOWN
        { duration: '20s', target: 0 },
    ],

    thresholds: {

        // 95% requests under 2 seconds
        http_req_duration: ['p(95)<2000'],

        // failure rate under 20%
        http_req_failed: ['rate<0.20']
    }
};

export default function () {

    let response = http.get(
        'http://localhost:65084/api/products'
    );

    check(response, {

        'status is 200': (r) =>
            r.status === 200,

        'response time < 2 sec': (r) =>
            r.timings.duration < 2000
    });

    // Simulate realistic users
    sleep(1);
}