import http from 'k6/http';
import { SharedArray } from 'k6/data';
import { check, sleep } from 'k6';

export let options = {
    vus: 20,
    duration: '20s',

    thresholds: {
        http_req_duration: ['p(95)<500'],
        check(response, {
    'expected response': (r) =>
        r.status === user.expectedStatus
});: ['rate<0.50']
    }
};

const users = new SharedArray(
    'users',
    function () {
        return JSON.parse(open('./users.json'));
    }
);

export default function () {

    // RANDOM USER
    let user =
        users[Math.floor(Math.random() * users.length)];

    // LOGIN PAYLOAD
    let payload = JSON.stringify({
        username: user.username,
        password: user.password
    });

    // REQUEST HEADERS
    let params = {
        headers: {
            'Content-Type': 'application/json'
        }
    };

    // LOGIN REQUEST
    let response = http.post(
        'http://localhost:65084/api/auth/login',
        payload,
        params
    );

    // RESPONSE VALIDATION
    check(response, {

        // EXPECTED STATUS
        'status is expected': (r) =>
            r.status === user.expectedStatus,

        // RESPONSE TIME
        'response time < 500ms': (r) =>
            r.timings.duration < 500,

        // SUCCESS TOKEN CHECK
        'token exists for valid users': (r) => {

            if (user.expectedStatus === 200) {

                let body = JSON.parse(r.body);

                return body.token !== undefined;
            }

            return true;
        }
    });

    // OPTIONAL DEBUG LOG
    console.log(
        `User: ${user.username} | ` +
        `Expected: ${user.expectedStatus} | ` +
        `Actual: ${response.status}`
    );

    // SMALL DELAY
    sleep(1);
}