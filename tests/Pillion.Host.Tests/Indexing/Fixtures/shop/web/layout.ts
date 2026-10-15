import { Circle, Rectangle, Shape } from "./shapes";

export function totalArea(shapes: Shape[]): number {
  return shapes.reduce((sum, shape) => sum + shape.area(), 0);
}

export function sampleLayout(): number {
  return totalArea([new Circle(2), new Rectangle(3, 4)]);
}
