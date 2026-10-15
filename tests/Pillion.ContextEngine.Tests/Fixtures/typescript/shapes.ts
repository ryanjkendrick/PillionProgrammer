import { Logger } from "./logger";

export interface Shape {
  area(): number;
  name: string;
}

/** A circle. */
export class Circle implements Shape {
  name = "circle";

  constructor(private readonly radius: number) {}

  /** The area. */
  area(): number {
    return Math.PI * this.radius * this.radius;
  }
}

export enum Colour {
  Red,
  Green,
}

export type ShapeList = Shape[];

export const describe = (shape: Shape): string => {
  return `${shape.name}: ${shape.area()}`;
};

namespace Geometry {
  export function unit(): Circle {
    return new Circle(1);
  }
}

const logger = new Logger();
logger.info("loaded");
