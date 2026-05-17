import http from 'k6/http';
import { check, sleep } from 'k6';

export let options = {

    stages: [

        // NORMAL LOAD
        { duration: '10s', target: 20 },

        // HUGE SPIKE
        { duration: '10s', target: 200 },

        // STAY UNDER HIGH LOAD
        { duration: '20s', target: 200 },

        // DROP USERS
        { duration: '10s', target: 20 },

        // FINISH
        { duration: '10s', target: 0 },
    ],

    thresholds: {

        http_req_duration: ['p(95)<1000'],

        http_req_failed: ['rate<0.10']
    }
};

export default function () {

    let response = http.get(
        'http://localhost:65084/api/products'
    );

    check(response, {

        'status is 200': (r) =>
            r.status === 200,

        'response time < 1000ms': (r) =>
            r.timings.duration < 1000
    });

    sleep(1);
}