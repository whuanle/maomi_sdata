import { record } from 'rrweb';
import { init as start, flush, type RumSdkOptions } from './index';

/** 浏览器托管入口，默认遮蔽输入内容并接入 rrweb。 */
export function init(options: RumSdkOptions): void {
  start({ ...options, replayRecorderFactory: options.replayRecorderFactory ?? ((emit) =>
    record({ emit, maskAllInputs: true, blockClass: 'sdata-private', checkoutEveryNms: 30000 }) ?? (() => {})) });
}

export { flush };
