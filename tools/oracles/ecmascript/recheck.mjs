// Fixed-input recheck of exactly the initial failed files after harness repair.
import {runSuite} from './test262.mjs';
await runSuite(true);
